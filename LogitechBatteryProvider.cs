using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DeskHardwareWidget.Logitech;

namespace DeskHardwareWidget
{
    // Standalone, read-only battery queries. No LogiCopy process, settings,
    // hooks, driver or external executable is used by this provider.
    internal sealed class LogitechBatteryProvider
    {
        private Dictionary<string, BatteryDeviceSnapshot> _previous = new Dictionary<string, BatteryDeviceSnapshot>();
        private readonly Dictionary<string, int> _missedPings = new Dictionary<string, int>();
        internal const int DisconnectedAfterMissedPings = 3;

        internal List<BatteryDeviceSnapshot> Read(CancellationToken token)
        {
            var readings = new Dictionary<string, BatteryDeviceSnapshot>();
            var endpoints = HidEndpoint.Discover();
            try
            {
                foreach (var group in endpoints.GroupBy(endpoint => endpoint.Group))
                {
                    token.ThrowIfCancellationRequested();
                    using (var transport = new HidTransport(group.ToList(), token))
                    {
                        bool bluetooth = !transport.IsReceiver && ((transport.ProductId & 0xF000) == 0xB000 ||
                            transport.Key.IndexOf("bth", StringComparison.OrdinalIgnoreCase) >= 0);
                        string connection = transport.IsReceiver ? (transport.ProductId == 0xC548 ? "Logi Bolt / USB" : "Ricevitore Logitech / USB") :
                            bluetooth ? "Bluetooth" : "USB";
                        foreach (byte slot in transport.IsReceiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF })
                        {
                            string id = !transport.IsReceiver && !string.IsNullOrEmpty(group.First().Container) ? group.First().Container :
                                "logitech-" + StableId(transport.Key) + "-" + slot;
                            BatteryDeviceSnapshot previous;
                            _previous.TryGetValue(id, out previous);
                            if (!transport.Ping(slot))
                            {
                                if (previous != null) readings[id] = MissedPing(previous);
                                continue;
                            }
                            ConfirmConnection(id);
                            var device = new BatteryDeviceSnapshot
                            {
                                Id = id, Name = group.First().Product, IsConnected = true,
                                IsBluetooth = bluetooth, IsLogitechHid = true, Connection = connection
                            };
                            try
                            {
                                device.Name = ReadName(transport, slot) ?? (previous != null ? previous.Name : device.Name);
                                device.Kind = BatteryMonitor.GuessKind(device.Name);
                                ushort featureId = 0x1004;
                                byte feature = transport.Feature(slot, featureId);
                                if (feature == 0) { featureId = 0x1000; feature = transport.Feature(slot, featureId); }
                                if (feature == 0)
                                {
                                    // Do not add wired devices with no battery service.
                                    if (previous != null) readings[id] = Unavailable(previous);
                                    continue;
                                }
                                bool supportsPercent = true;
                                if (featureId == 0x1004)
                                {
                                    byte[] capabilities = transport.Request(slot, feature, 0, new byte[0]);
                                    supportsPercent = capabilities != null && capabilities.Length >= 2 && (capabilities[1] & 2) != 0;
                                }
                                byte[] response = transport.Request(slot, feature, featureId == 0x1004 ? (byte)0x10 : (byte)0, new byte[0]);
                                ApplyBattery(device, featureId, response, supportsPercent);
                                readings[id] = device;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Diagnostics.Write("Lettura batteria Logitech fallita", ex);
                                readings[id] = Unavailable(device);
                            }
                        }
                    }
                }
            }
            finally
            {
                // Also close collections from groups not yet visited if cancelled.
                foreach (var endpoint in endpoints) endpoint.Dispose();
            }
            foreach (string id in _missedPings.Keys.Where(id => !readings.ContainsKey(id)).ToArray()) _missedPings.Remove(id);
            _previous = readings;
            return readings.Values.ToList();
        }

        internal void ConfirmConnection(string id)
        {
            // A successful ping proves reachability even if the subsequent
            // battery request fails. Only failed pings count as an absent mouse.
            _missedPings.Remove(id);
        }

        internal BatteryDeviceSnapshot MissedPing(BatteryDeviceSnapshot previous)
        {
            int misses;
            _missedPings.TryGetValue(previous.Id, out misses);
            misses = Math.Min(DisconnectedAfterMissedPings, misses + 1);
            _missedPings[previous.Id] = misses;
            var device = Unavailable(previous);
            if (misses >= DisconnectedAfterMissedPings)
            {
                device.IsConnected = false;
                device.IsReadingUnavailable = false;
                device.BatteryStatusText = null;
            }
            return device;
        }

        private static string ReadName(HidTransport transport, byte slot)
        {
            byte feature = transport.Feature(slot, 0x0005);
            if (feature == 0) return null;
            byte[] response = transport.Request(slot, feature, 0, new byte[0]);
            if (response == null || response.Length == 0 || response[0] == 0 || response[0] > 128) return null;
            int length = response[0];
            var bytes = new List<byte>();
            while (bytes.Count < length)
            {
                response = transport.Request(slot, feature, 0x10, new byte[] { (byte)bytes.Count });
                if (response == null || response.Length == 0) return null;
                bytes.AddRange(response.Take(Math.Min(length - bytes.Count, response.Length)));
            }
            string name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        internal static BatteryDeviceSnapshot Unavailable(BatteryDeviceSnapshot previous)
        {
            return new BatteryDeviceSnapshot
            {
                // No reply does not establish a physical disconnection. Keep
                // the known device visible without retaining old battery data.
                Id = previous.Id, Name = previous.Name, Kind = previous.Kind, IsConnected = null,
                IsReadingUnavailable = true, BatteryStatusText = "Lettura non disponibile",
                IsLogitechHid = true, IsBluetooth = previous.IsBluetooth, Connection = previous.Connection
            };
        }

        internal static void ApplyBattery(BatteryDeviceSnapshot device, ushort featureId, byte[] data, bool supportsPercent)
        {
            device.Percent = null; device.IsCharging = null; device.BatteryStatusText = "Lettura non disponibile"; device.ReportedAtUtc = null;
            device.IsReadingUnavailable = true;
            if ((featureId != 0x1004 && featureId != 0x1000) || data == null || data.Length < 3) return;
            device.IsReadingUnavailable = false; device.BatteryStatusText = null;
            // 0x1004 has a capability bit for percentage; a level-only device
            // reports zero in this byte. In 0x1000, zero explicitly means unknown.
            if (supportsPercent && (featureId == 0x1004 || data[0] != 0)) device.Percent = BatteryMonitor.ValidPercent(data[0]);
            device.ReportedAtUtc = DateTime.UtcNow;
            switch (data[2])
            {
                case 0: device.IsCharging = false; device.BatteryStatusText = "A batteria"; break;
                case 1: device.IsCharging = true; break;
                case 2:
                    device.IsCharging = true;
                    device.BatteryStatusText = featureId == 0x1004 ? "Ricarica lenta" : "Ricarica quasi completa";
                    break;
                case 3: device.IsCharging = false; device.BatteryStatusText = "Carica completa"; break;
                case 4:
                    device.IsCharging = featureId == 0x1000;
                    device.BatteryStatusText = featureId == 0x1000 ? "Ricarica lenta" : "Errore di ricarica";
                    break;
                case 5: case 6: case 7:
                    if (featureId == 0x1000) { device.IsCharging = false; device.BatteryStatusText = "Errore di ricarica"; }
                    break;
            }
        }

        internal static List<BatteryDeviceSnapshot> Merge(IEnumerable<BatteryDeviceSnapshot> windows, IEnumerable<BatteryDeviceSnapshot> logitech)
        {
            var merged = windows.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var device in logitech)
            {
                BatteryDeviceSnapshot reported;
                if (merged.TryGetValue(device.Id, out reported))
                {
                    // Repeated failed pings override cached Windows levels.
                    // Otherwise an absent mouse could retain an old percentage.
                    if (device.IsConnected == false) { merged[device.Id] = device; continue; }
                    // A failed HID query must not hide valid Windows data. A
                    // successful battery query can supply the missing charge state.
                    if (!device.ReportedAtUtc.HasValue && (reported.Percent.HasValue || reported.IsConnected == false)) continue;
                    if (!device.Percent.HasValue) device.Percent = reported.Percent;
                    if (!device.IsCharging.HasValue) device.IsCharging = reported.IsCharging;
                }
                merged[device.Id] = device;
            }
            return merged.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string StableId(string value)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char character in value) { hash ^= character; hash = unchecked(hash * 1099511628211UL); }
            return hash.ToString("x16");
        }
    }
}
