using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskHardwareWidget
{
    public sealed class BatteryDeviceSnapshot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public bool IsPc { get; set; }
        public bool IsBluetooth { get; set; }
        public bool IsLogitechHid { get; set; }
        public bool IsReadingUnavailable { get; set; }
        public bool IsDualSenseHid { get; set; }
        internal string WindowsContainerId { get; set; }
        internal string[] WindowsContainerIds { get; set; }
        public string Connection { get; set; }
        public string BatteryStatusText { get; set; }
        public int? Percent { get; set; }
        public bool? IsConnected { get; set; }
        public bool? IsCharging { get; set; }
        public bool? IsOnAc { get; set; }
        public int? RemainingSeconds { get; set; }
        public DateTime? ReportedAtUtc { get; set; }
        public string StatusText
        {
            get
            {
                if (IsConnected == false) return "Scollegato";
                if (!string.IsNullOrEmpty(BatteryStatusText)) return BatteryStatusText;
                if (IsCharging == true) return "In carica";
                if (IsPc && IsOnAc == true) return Percent == 100 ? "Carica completa" : "Alimentatore collegato";
                if (IsPc && IsOnAc == false) return "A batteria";
                if (IsCharging == false) return "Non in carica";
                return IsConnected == true ? "Connesso" : "Stato non disponibile";
            }
        }
    }

    public sealed class BatterySnapshot
    {
        public DateTime CapturedAtUtc { get; set; }
        public List<BatteryDeviceSnapshot> Devices { get; set; }
        public string Note { get; set; }
        public BatterySnapshot() { CapturedAtUtc = DateTime.UtcNow; Devices = new List<BatteryDeviceSnapshot>(); }
    }

    // Windows properties plus direct Logitech HID++ and DualSense HID readings.
    internal sealed class BatteryMonitor
    {
        private List<BatteryDeviceSnapshot> _accessories = new List<BatteryDeviceSnapshot>();
        private string _accessoryError;
        private readonly LogitechBatteryProvider _logitech = new LogitechBatteryProvider();
        private readonly DualSenseBatteryProvider _dualSense = new DualSenseBatteryProvider();
        internal sealed class DeviceNode
        {
            public string Id, Container, Name;
            public bool IsRoot, IsBluetooth, IsUsbController;
            public int? Percent;
            public bool? Connected, Charging;
            public DateTime? ReportedAtUtc;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PowerStatus
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid Format; public uint Id;
            public PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devices, ref DeviceInfo info, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr devices, ref DeviceInfo info, ref PropertyKey key,
            out uint type, byte[] data, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

        private const string DeviceProperties = "A45C254E-DF1C-4EFD-8020-67D146A850E0";
        private const string BluetoothBattery = "104EA319-6EE2-4701-BD47-8DDBF425BBE5";
        private const string ShellBattery = "49CD1F76-5626-4B17-A4E8-18B4AA1A2213";

        internal BatterySnapshot Update(bool refreshAccessories, CancellationToken token = default(CancellationToken))
        {
            var snapshot = new BatterySnapshot();
            PowerStatus power;
            if (GetSystemPowerStatus(out power))
            {
                var pc = CreatePc(power);
                if (pc != null) snapshot.Devices.Add(pc);
            }
            else snapshot.Note = "Lettura della batteria PC non disponibile";
            if (refreshAccessories)
            {
                try { _accessories = BuildAccessories(ReadDeviceNodes()); _accessoryError = null; }
                catch (Exception ex)
                {
                    _accessories = new List<BatteryDeviceSnapshot>();
                    _accessoryError = "Lettura accessori non disponibile";
                    Diagnostics.Write("Lettura batterie accessori fallita", ex);
                }
                try { _accessories = LogitechBatteryProvider.Merge(_accessories, _logitech.Read(token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _accessoryError = "Lettura batterie Logitech non disponibile";
                    Diagnostics.Write("Lettura batterie Logitech fallita", ex);
                }
                try { _accessories = DualSenseBatteryProvider.Merge(_accessories, _dualSense.Read(token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _accessoryError = "Lettura batterie DualSense non disponibile";
                    Diagnostics.Write("Lettura batterie DualSense fallita", ex);
                }
            }
            snapshot.Devices.AddRange(_accessories);
            if (_accessoryError != null) snapshot.Note = _accessoryError;
            return snapshot;
        }

        internal static BatteryDeviceSnapshot CreatePc(PowerStatus power)
        {
            // 255 is an unknown flag byte, not "no battery" or "charging".
            if (power.BatteryFlag != 255 && (power.BatteryFlag & 128) != 0) return null;
            return new BatteryDeviceSnapshot
            {
                Id = "system-battery", Name = "Questo PC", Kind = "Laptop", IsPc = true, IsConnected = true,
                Percent = power.BatteryLifePercent <= 100 ? (int?)power.BatteryLifePercent : null,
                IsCharging = power.BatteryFlag == 255 ? null : (bool?)((power.BatteryFlag & 8) != 0),
                IsOnAc = power.AcLineStatus == 255 ? null : (bool?)(power.AcLineStatus == 1),
                RemainingSeconds = power.AcLineStatus == 0 && power.BatteryLifeTime != uint.MaxValue && power.BatteryLifeTime <= int.MaxValue ? (int?)power.BatteryLifeTime : null
            };
        }

        private static List<DeviceNode> ReadDeviceNodes()
        {
            var nodes = new List<DeviceNode>();
            IntPtr devices = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 2 | 4);
            if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; ; index++)
                {
                    var info = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(devices, index, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) throw new Win32Exception(error);
                        break;
                    }
                    // The aggregate system battery is already read above.
                    if (info.ClassGuid == new Guid("72631E54-78A4-11D0-BCF7-00AA00B7B32A")) continue;
                    uint required;
                    var idBuffer = new StringBuilder(2048);
                    if (!SetupDiGetDeviceInstanceIdW(devices, ref info, idBuffer, (uint)idBuffer.Capacity, out required)) continue;
                    string id = idBuffer.ToString();
                    bool bluetooth = id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
                    bool root = id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
                    string controllerName = DualSenseBatteryProvider.UsbControllerName(id);
                    int? percent = ValidPercent(ReadProperty(devices, ref info, new PropertyKey(BluetoothBattery, 2))) ??
                                   ValidPercent(ReadProperty(devices, ref info, new PropertyKey(ShellBattery, 10)));
                    // Keep present Sony USB controllers even when Windows has
                    // no battery property or the HID stream is inaccessible.
                    if (!root && !percent.HasValue && controllerName == null) continue;
                    string name = ReadProperty(devices, ref info, new PropertyKey(DeviceProperties, 14)) as string ??
                                  ReadProperty(devices, ref info, new PropertyKey(DeviceProperties, 2)) as string;
                    if (string.IsNullOrWhiteSpace(name)) name = "Dispositivo";
                    if (controllerName != null) name = controllerName;
                    object container = ReadProperty(devices, ref info, new PropertyKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2));
                    object charging = ReadProperty(devices, ref info, new PropertyKey(ShellBattery, 11));
                    object reportTime = ReadProperty(devices, ref info, new PropertyKey(BluetoothBattery, 7));
                    var connected = ReadProperty(devices, ref info, new PropertyKey("83DA6326-97A6-4088-9453-A1923F573B29", 15)) as bool?;
                    nodes.Add(new DeviceNode
                    {
                        Id = id, IsRoot = root || controllerName != null && id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase),
                        IsBluetooth = bluetooth, IsUsbController = controllerName != null, Name = name,
                        Container = container is Guid && (Guid)container != Guid.Empty ? container.ToString() : id,
                        Percent = percent, Connected = controllerName != null ? true : connected,
                        Charging = charging is byte && (byte)charging <= 1 ? (bool?)((byte)charging == 1) : null,
                        ReportedAtUtc = reportTime as DateTime?
                    });
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
            return nodes;
        }

        internal static int? ValidPercent(object value)
        {
            if (!(value is byte) && !(value is ushort) && !(value is uint) && !(value is int)) return null;
            long number = Convert.ToInt64(value);
            return number >= 0 && number <= 100 ? (int?)number : null;
        }

        internal static List<BatteryDeviceSnapshot> BuildAccessories(IEnumerable<DeviceNode> nodes)
        {
            return nodes.GroupBy(node => node.Container, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                bool usbController = group.Any(node => node.IsUsbController);
                var root = group.OrderByDescending(node => node.IsUsbController).FirstOrDefault(node => node.IsRoot);
                var reading = group.Where(node => !usbController || !node.IsBluetooth)
                    .OrderByDescending(node => node.Percent.HasValue).ThenByDescending(node => node.ReportedAtUtc ?? DateTime.MinValue).First();
                bool? connected = usbController ? true : root != null && root.Connected.HasValue ? root.Connected : group.Select(node => node.Connected).FirstOrDefault(value => value.HasValue);
                string name = root != null ? root.Name : reading.Name;
                return new BatteryDeviceSnapshot
                {
                    Id = group.Key, Name = name, Kind = usbController ? "Gamepad" : GuessKind(name), IsBluetooth = !usbController && group.Any(node => node.IsBluetooth),
                    Connection = usbController ? "USB" : null,
                    IsReadingUnavailable = usbController && !reading.Percent.HasValue,
                    BatteryStatusText = usbController && !reading.Percent.HasValue ? "Batteria non disponibile" : null,
                    Percent = connected == false ? null : reading.Percent, IsConnected = connected,
                    IsCharging = connected == false ? null : reading.Charging, ReportedAtUtc = reading.ReportedAtUtc
                };
            }).OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static string GuessKind(string name)
        {
            name = (name ?? "").ToLowerInvariant();
            if (new[] { "mouse", "master", "mx anywhere", "g502", "g pro" }.Any(name.Contains)) return "Mouse";
            if (new[] { "head", "cuff", "buds", "airpod", "ear", "wh-", "wf-" }.Any(name.Contains)) return "Headphones";
            if (new[] { "keyboard", "tastiera", "keys" }.Any(name.Contains)) return "Keyboard";
            if (new[] { "xbox", "controller", "gamepad", "dualsense", "dualshock" }.Any(name.Contains)) return "Gamepad";
            if (new[] { "phone", "galaxy", "pixel", "telefono" }.Any(name.Contains)) return "Phone";
            if (new[] { "watch", "orologio" }.Any(name.Contains)) return "Watch";
            return "Device";
        }

        private static object ReadProperty(IntPtr devices, ref DeviceInfo info, PropertyKey key)
        {
            uint type, required;
            var data = new byte[4096];
            if (!SetupDiGetDevicePropertyW(devices, ref info, ref key, out type, data, (uint)data.Length, out required, 0)) return null;
            if (type == 3 && required >= 1) return data[0];
            if (type == 5 && required >= 2) return BitConverter.ToUInt16(data, 0);
            if (type == 7 && required >= 4) return BitConverter.ToUInt32(data, 0);
            if (type == 17 && required >= 1) return data[0] != 0;
            if (type == 18) return Encoding.Unicode.GetString(data, 0, (int)required).TrimEnd('\0');
            if (type == 13 && required >= 16) { var guid = new byte[16]; Array.Copy(data, guid, 16); return new Guid(guid); }
            if (type == 16 && required >= 8)
            {
                long time = BitConverter.ToInt64(data, 0);
                if (time > 0 && time <= DateTime.UtcNow.AddDays(1).ToFileTimeUtc()) return DateTime.FromFileTimeUtc(time);
            }
            return null;
        }
    }
}
