using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using HidSharp;
using HidNative = DeskHardwareWidget.Logitech.Native;

namespace DeskHardwareWidget
{
    // Independent .NET Framework implementation of the HID protocol documented by
    // DualSenseClient, revision 5e9c019b138e15ca85949994d9258f895940b84e.
    // See licenses/DualSense-protocol-reference.txt for the exact source references.
    // Only input and GET_FEATURE reports are used; no controller outputs are sent.
    internal sealed class DualSenseBatteryProvider
    {
        private Dictionary<string, BatteryDeviceSnapshot> _previous = new Dictionary<string, BatteryDeviceSnapshot>(StringComparer.OrdinalIgnoreCase);

        internal List<BatteryDeviceSnapshot> Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var devices = DeviceList.Local.GetHidDevices(0x054C).Where(device => IsSupported(device.VendorID, device.ProductID)).ToList();
            var containers = ReadContainers(token);
            var readings = new List<BatteryDeviceSnapshot>();
            foreach (var hid in devices)
            {
                token.ThrowIfCancellationRequested();
                string container;
                containers.TryGetValue(hid.DevicePath, out container);
                var previous = _previous.Values.FirstOrDefault(item => container != null &&
                    (item.WindowsContainerIds ?? new[] { item.WindowsContainerId }).Contains(container, StringComparer.OrdinalIgnoreCase));
                var device = new BatteryDeviceSnapshot
                {
                    Id = previous != null ? previous.Id : container ?? "dualsense-" + hid.DevicePath.ToLowerInvariant(),
                    WindowsContainerId = container, Name = hid.ProductID == 0x0DF2 ? "DualSense Edge" : "DualSense",
                    Kind = "Gamepad", IsDualSenseHid = true, IsReadingUnavailable = true, BatteryStatusText = "Batteria non disponibile"
                };
                try
                {
                    int length = hid.GetMaxInputReportLength();
                    // The USB gamepad collection is 64 bytes; Bluetooth is 78.
                    // Ignore other HID collections (including Edge vendor interfaces).
                    if (length != 64 && length != 78) continue;
                    device.IsBluetooth = length == 78;
                    device.Connection = device.IsBluetooth ? "Bluetooth" : "USB";
                    // A present USB interface establishes the wired connection
                    // even when another app prevents opening its input stream.
                    if (!device.IsBluetooth) device.IsConnected = true;
                    HidStream stream;
                    if (hid.TryOpen(out stream))
                    {
                        using (stream)
                        {
                            stream.ReadTimeout = 100;
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                var pairing = new byte[20]; pairing[0] = 0x09;
                                stream.GetFeature(pairing);
                                string address = ReadControllerAddress(pairing);
                                if (address != null) device.Id = "dualsense-" + address;
                            }
                            catch (IOException) { /* Container/path remains a valid identity. */ }
                            token.ThrowIfCancellationRequested();
                            if (device.IsBluetooth)
                            {
                                // GET calibration also enables full 0x31 input on a
                                // controller still sending simple Bluetooth reports.
                                // This reads calibration; it does not change calibration.
                                try { var calibration = new byte[41]; calibration[0] = 0x05; stream.GetFeature(calibration); }
                                catch (IOException) { /* Full reports may already be enabled by a game. */ }
                            }
                            var buffer = new byte[length];
                            var watch = Stopwatch.StartNew();
                            while (watch.ElapsedMilliseconds < 750)
                            {
                                token.ThrowIfCancellationRequested();
                                int count;
                                try { count = stream.Read(buffer, 0, buffer.Length); }
                                catch (TimeoutException) { continue; }
                                if (count <= 0) continue;
                                device.IsConnected = true;
                                if (TryApplyReport(device, buffer, count, device.IsBluetooth)) break;
                            }
                            if (!device.ReportedAtUtc.HasValue) device.BatteryStatusText = "Batteria non disponibile";
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Diagnostics.Write("Lettura batteria DualSense fallita", ex);
                }
                readings.Add(device);
            }
            token.ThrowIfCancellationRequested();
            return CompleteScan(readings);
        }

        internal static bool IsSupported(int vendor, int product)
        {
            return vendor == 0x054C && (product == 0x0CE6 || product == 0x0DF2);
        }

        internal static string UsbControllerName(string instanceId)
        {
            var match = Regex.Match(instanceId ?? "", @"^(?:USB|HID)\\VID_054C&PID_(0CE6|0DF2)(?=&|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return !match.Success ? null : match.Groups[1].Value.Equals("0DF2", StringComparison.OrdinalIgnoreCase) ? "DualSense Edge" : "DualSense";
        }

        internal static string ReadControllerAddress(byte[] pairing)
        {
            if (pairing == null || pairing.Length < 16 || pairing[0] != 0x09) return null;
            var mac = pairing.Skip(1).Take(6).Reverse().ToArray();
            if (mac.All(value => value == 0) || mac.All(value => value == 255)) return null;
            return BitConverter.ToString(mac).Replace("-", "").ToLowerInvariant();
        }

        internal static bool TryApplyReport(BatteryDeviceSnapshot device, byte[] data, int count, bool bluetooth)
        {
            device.Percent = null; device.IsCharging = null; device.IsOnAc = null;
            device.BatteryStatusText = null; device.ReportedAtUtc = null;
            device.IsReadingUnavailable = true;
            if (data == null || count > data.Length || count < (bluetooth ? 78 : 64)) return false;
            if (data[0] != (bluetooth ? 0x31 : 0x01)) return false;
            if (bluetooth && (count != 78 || !HasValidBluetoothCrc(data))) return false;
            int start = bluetooth ? 2 : 1;
            int level = data[start + 52] & 0x0F;
            int state = data[start + 52] >> 4;
            device.IsOnAc = (data[start + 53] & 0x10) != 0;
            device.IsConnected = true;
            device.IsReadingUnavailable = false;
            device.ReportedAtUtc = DateTime.UtcNow;
            switch (state)
            {
                case 0:
                    if (level <= 10) device.Percent = Math.Min(100, level * 10 + 5);
                    device.IsCharging = false;
                    device.BatteryStatusText = device.IsOnAc == true ? "Collegato · non in carica" : "A batteria";
                    break;
                case 1:
                    if (level <= 10) device.Percent = Math.Max(0, (level - 1) * 10);
                    device.IsCharging = true;
                    break;
                case 2:
                    if (level <= 10) device.Percent = 100;
                    device.IsCharging = false; device.BatteryStatusText = "Carica completa";
                    break;
                case 0xA: device.IsCharging = false; device.BatteryStatusText = "Tensione anomala"; break;
                case 0xB: device.IsCharging = false; device.BatteryStatusText = "Temperatura anomala"; break;
                case 0xF: device.IsCharging = false; device.BatteryStatusText = "Errore di ricarica"; break;
                default: device.BatteryStatusText = "Stato batteria sconosciuto"; break;
            }
            return true;
        }

        private static bool HasValidBluetoothCrc(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            // Bluetooth HID input CRC includes the transaction prefix 0xA1.
            for (int index = -1; index < 74; index++)
            {
                crc ^= index < 0 ? (byte)0xA1 : data[index];
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0U);
            }
            return ~crc == BitConverter.ToUInt32(data, 74);
        }

        internal List<BatteryDeviceSnapshot> CompleteScan(IEnumerable<BatteryDeviceSnapshot> readings)
        {
            var result = readings.ToList();
            foreach (var group in result.GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase))
            {
                BatteryDeviceSnapshot previous;
                _previous.TryGetValue(group.Key, out previous);
                var aliases = group.Select(device => device.WindowsContainerId)
                    .Concat(previous == null ? new string[0] : previous.WindowsContainerIds ?? new[] { previous.WindowsContainerId })
                    .Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var device in group) device.WindowsContainerIds = aliases;
            }
            foreach (var previous in _previous.Values)
            {
                if (!result.Any(device => string.Equals(device.Id, previous.Id, StringComparison.OrdinalIgnoreCase) ||
                    previous.WindowsContainerId != null && string.Equals(device.WindowsContainerId, previous.WindowsContainerId, StringComparison.OrdinalIgnoreCase)))
                    result.Add(Unavailable(previous));
            }
            _previous = result.GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => BestReading(group), StringComparer.OrdinalIgnoreCase);
            return result;
        }

        internal static BatteryDeviceSnapshot Unavailable(BatteryDeviceSnapshot previous)
        {
            return new BatteryDeviceSnapshot
            {
                Id = previous.Id, WindowsContainerId = previous.WindowsContainerId, Name = previous.Name, Kind = "Gamepad",
                WindowsContainerIds = previous.WindowsContainerIds,
                IsDualSenseHid = true, IsBluetooth = previous.IsBluetooth, Connection = previous.Connection, IsConnected = false
            };
        }

        private static BatteryDeviceSnapshot BestReading(IEnumerable<BatteryDeviceSnapshot> readings)
        {
            return readings.OrderByDescending(device => device.IsConnected == true)
                .ThenByDescending(device => device.ReportedAtUtc.HasValue).ThenBy(device => device.IsBluetooth).First();
        }

        internal static List<BatteryDeviceSnapshot> Merge(IEnumerable<BatteryDeviceSnapshot> windows, IEnumerable<BatteryDeviceSnapshot> controllers)
        {
            var result = windows.ToList();
            foreach (var group in controllers.GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase))
            {
                var device = BestReading(group);
                var aliases = new HashSet<string>(group.SelectMany(item => item.WindowsContainerIds ?? new[] { item.WindowsContainerId })
                    .Where(id => id != null), StringComparer.OrdinalIgnoreCase);
                aliases.Add(device.Id);
                var matching = result.Where(item => aliases.Contains(item.Id)).ToList();
                // If opening HID failed, keep a usable Windows reading. A valid
                // full report (even an error/unknown state) is authoritative.
                if (!device.ReportedAtUtc.HasValue && matching.Any(item => item.IsConnected != false && item.Percent.HasValue)) continue;
                if (!device.ReportedAtUtc.HasValue && matching.Any(item => item.IsConnected == true && item.Kind == "Gamepad" && item.Connection == "USB"))
                {
                    // PnP can already see USB while HID is busy, hidden or still
                    // starting. Its confirmed presence wins over an old offline HID entry.
                    device.IsConnected = true; device.IsBluetooth = false; device.Connection = "USB";
                    device.IsReadingUnavailable = true; device.BatteryStatusText = "Batteria non disponibile";
                }
                result.RemoveAll(item => aliases.Contains(item.Id));
                result.Add(device);
            }
            return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static Dictionary<string, string> ReadContainers(CancellationToken token)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Guid guid; HidNative.HidD_GetHidGuid(out guid);
            IntPtr set = HidNative.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) return result;
            try
            {
                for (int index = 0; ; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var info = new HidNative.InterfaceData { cbSize = Marshal.SizeOf(typeof(HidNative.InterfaceData)) };
                    if (!HidNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref info)) break;
                    int needed;
                    HidNative.SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        var device = new HidNative.DeviceInfo { Size = Marshal.SizeOf(typeof(HidNative.DeviceInfo)) };
                        if (!HidNative.SetupDiGetDeviceInterfaceDetail(set, ref info, detail, needed, out needed, ref device)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        var key = new HidNative.PropertyKey { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
                        uint type, size; var bytes = new byte[16];
                        if (HidNative.SetupDiGetDevicePropertyW(set, ref device, ref key, out type, bytes, 16, out size, 0) && type == 13 && size == 16)
                        {
                            Guid container = new Guid(bytes);
                            if (container != Guid.Empty) result[path] = container.ToString();
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { HidNative.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }
    }
}
