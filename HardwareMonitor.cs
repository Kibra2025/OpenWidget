using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace DeskHardwareWidget
{
    public class GpuSnapshot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public double LoadPercent { get; set; }
        public bool HasLoad { get; set; }
        public double TemperatureC { get; set; }
        public bool HasTemperature { get; set; }
        public double MemoryUsedMb { get; set; }
        public double MemoryTotalMb { get; set; }
        public bool HasMemory { get; set; }
        public bool IsSharedMemory { get; set; }
        public string TemperatureText { get; set; }
        public string MemoryText { get; set; }
    }

    public class HardwareSnapshot
    {
        public DateTime CapturedAtUtc { get; set; }
        public double CpuLoadPercent { get; set; }
        public bool HasCpuLoad { get; set; }
        public double CpuTempC { get; set; }
        public bool HasCpuTemp { get; set; }
        public string CpuName { get; set; }
        public string CpuSubtitle { get; set; }
        public bool PawnIoRequired { get; set; }
        public List<GpuSnapshot> Gpus { get; set; }
        public double RamLoadPercent { get; set; }
        public double RamUsedGb { get; set; }
        public double RamTotalGb { get; set; }
        public bool HasRam { get; set; }
        public string RamSubtitle { get; set; }
        public double DownloadSpeedKbps { get; set; }
        public double UploadSpeedKbps { get; set; }
        public string DownloadFormatted { get; set; }
        public string UploadFormatted { get; set; }
        public string NetworkInterfaceName { get; set; }

        public HardwareSnapshot()
        {
            CapturedAtUtc = DateTime.UtcNow;
            CpuName = "CPU"; CpuSubtitle = "Temperatura non disponibile";
            Gpus = new List<GpuSnapshot>();
            RamSubtitle = "Memoria non disponibile";
            DownloadFormatted = "0 Kbps"; UploadFormatted = "0 Kbps"; NetworkInterfaceName = "Nessuna connessione";
        }
    }

    internal class SensorReading
    {
        public SensorType Type;
        public string Name;
        public double? Value;
        public SensorReading(SensorType type, string name, double? value) { Type = type; Name = name; Value = value; }
    }

    public sealed class HardwareMonitor : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
        [StructLayout(LayoutKind.Sequential)]
        private class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhys, AvailablePhys, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

        private Computer _computer;
        private long _idle, _kernel, _user;
        private bool _hasCpuTimes;
        private bool _disposed;
        private string _cpuName = "Processore CPU";
        private readonly List<GpuSnapshot> _fallbackGpus = new List<GpuSnapshot>();
        private readonly Dictionary<string, NetworkCounters> _networkCounters = new Dictionary<string, NetworkCounters>();
        private long _networkTimestamp;
        private sealed class NetworkCounters { public long Received, Sent; }

        public HardwareMonitor()
        {
            _hasCpuTimes = GetSystemTimes(out _idle, out _kernel, out _user);
            DiscoverDevices();
            try
            {
                _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
                _computer.Open();
            }
            catch (Exception ex) { Diagnostics.Write("Inizializzazione dei sensori hardware fallita", ex); }
        }

        private void DiscoverDevices()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                using (var results = searcher.Get())
                    foreach (ManagementObject item in results)
                    {
                        using (item) _cpuName = Convert.ToString(item["Name"]).Replace("(TM)", "").Replace("(R)", "").Trim();
                        break;
                    }
                using (var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                using (var results = searcher.Get())
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            string name = Convert.ToString(item["Name"]);
                            if (!string.IsNullOrWhiteSpace(name))
                                _fallbackGpus.Add(CreateGpuSnapshot(Convert.ToString(item["PNPDeviceID"]), name, "GPU", new List<SensorReading>()));
                        }
                    }
            }
            catch (Exception ex) { Diagnostics.Write("Identificazione dei dispositivi fallita", ex); }
        }

        public HardwareSnapshot Update()
        {
            if (_disposed) throw new ObjectDisposedException("HardwareMonitor");
            var snapshot = new HardwareSnapshot { CpuName = _cpuName, PawnIoRequired = !Win32Helper.IsPawnIoInstalled() };
            ReadCpuLoad(snapshot);
            if (_computer != null)
            {
                foreach (IHardware hardware in _computer.Hardware)
                {
                    try
                    {
                        UpdateHardware(hardware);
                        var sensors = ReadSensors(hardware);
                        if (hardware.HardwareType == HardwareType.Cpu)
                        {
                            snapshot.CpuName = hardware.Name;
                            double? temperature = SelectTemperature(sensors, true);
                            if (temperature.HasValue)
                            {
                                snapshot.CpuTempC = temperature.Value;
                                snapshot.HasCpuTemp = true;
                                snapshot.CpuSubtitle = string.Format("{0:0.0} °C", temperature.Value);
                            }
                        }
                        else if (IsGpu(hardware.HardwareType))
                            snapshot.Gpus.Add(CreateGpuSnapshot(hardware.Identifier.ToString(), hardware.Name, hardware.HardwareType.ToString(), sensors));
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Write("Lettura del dispositivo fallita: " + hardware.Name, ex);
                        if (IsGpu(hardware.HardwareType))
                            snapshot.Gpus.Add(CreateGpuSnapshot(hardware.Identifier.ToString(), hardware.Name, hardware.HardwareType.ToString(), new List<SensorReading>()));
                    }
                }
            }
            foreach (var gpu in _fallbackGpus)
                if (!snapshot.Gpus.Any(g => string.Equals(g.Name, gpu.Name, StringComparison.OrdinalIgnoreCase))) snapshot.Gpus.Add(gpu);
            snapshot.Gpus = snapshot.Gpus.OrderBy(g => g.Kind == "GpuIntel" || g.Name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                .ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
            if (!snapshot.HasCpuTemp)
                snapshot.CpuSubtitle = snapshot.PawnIoRequired ? "Installa PawnIO per la temp." :
                    (Win32Helper.IsRunningAsAdministrator() ? "Sensore CPU non disponibile" : "Avvia come amministratore");
            ReadMemory(snapshot);
            ReadNetwork(snapshot);
            return snapshot;
        }

        private static bool IsGpu(HardwareType type)
        {
            return type == HardwareType.GpuNvidia || type == HardwareType.GpuAmd || type == HardwareType.GpuIntel;
        }
        private static void UpdateHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var child in hardware.SubHardware) UpdateHardware(child);
        }
        private static List<SensorReading> ReadSensors(IHardware hardware)
        {
            var readings = hardware.Sensors.Select(s => new SensorReading(s.SensorType, s.Name, s.Value.HasValue ? (double?)s.Value.Value : null)).ToList();
            foreach (var child in hardware.SubHardware) readings.AddRange(ReadSensors(child));
            return readings;
        }
        private static bool IsValid(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }
        internal static double? SelectTemperature(IList<SensorReading> sensors, bool cpu)
        {
            var temperatures = sensors.Where(s => s.Type == SensorType.Temperature && IsValid(s.Value) && s.Value >= -50 && s.Value <= 150 &&
                s.Name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) < 0 &&
                s.Name.IndexOf("Junction", StringComparison.OrdinalIgnoreCase) < 0 &&
                s.Name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) < 0 &&
                s.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            string[] preferences = cpu ? new[] { "CPU Package", "Tdie", "Core Max", "Core Average", "Tctl" } : new[] { "GPU Core", "GT Cores", "GPU Temperature" };
            foreach (string name in preferences)
            {
                var preferred = temperatures.FirstOrDefault(s => s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (preferred != null) return preferred.Value;
            }
            return temperatures.Count == 0 ? null : temperatures.Max(s => s.Value);
        }
        internal static GpuSnapshot CreateGpuSnapshot(string id, string name, string kind, IList<SensorReading> sensors)
        {
            var gpu = new GpuSnapshot { Id = id, Name = name, Kind = kind, TemperatureText = "Temp. non disponibile", MemoryText = "Memoria non disponibile" };
            double? temperature = SelectTemperature(sensors, false);
            if (temperature.HasValue)
            {
                gpu.HasTemperature = true; gpu.TemperatureC = temperature.Value;
                gpu.TemperatureText = string.Format("{0:0.0} °C", temperature.Value);
            }
            var core = sensors.FirstOrDefault(s => s.Type == SensorType.Load && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) && IsValid(s.Value));
            var engines = sensors.Where(s => s.Type == SensorType.Load && s.Name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase) && IsValid(s.Value)).ToList();
            double? load = core != null ? core.Value : (engines.Count == 0 ? null : engines.Max(s => s.Value));
            if (load.HasValue) { gpu.HasLoad = true; gpu.LoadPercent = Math.Max(0, Math.Min(100, load.Value)); }
            var used = FindMemory(sensors, "GPU Memory Used");
            var total = FindMemory(sensors, "GPU Memory Total");
            if (used == null) { used = FindMemory(sensors, "D3D Dedicated Memory Used"); total = FindMemory(sensors, "D3D Dedicated Memory Total"); }
            if (used == null) { used = FindMemory(sensors, "D3D Shared Memory Used"); total = FindMemory(sensors, "D3D Shared Memory Total"); gpu.IsSharedMemory = true; }
            if (used != null)
            {
                gpu.HasMemory = true; gpu.MemoryUsedMb = used.Value.Value;
                gpu.MemoryTotalMb = total == null ? 0 : total.Value.Value;
                string prefix = gpu.IsSharedMemory ? "Condivisa: " : "VRAM: ";
                gpu.MemoryText = total == null ? string.Format("{0}{1:0} MB", prefix, gpu.MemoryUsedMb) :
                    string.Format("{0}{1:0} / {2:0} MB", prefix, gpu.MemoryUsedMb, gpu.MemoryTotalMb);
            }
            return gpu;
        }
        private static SensorReading FindMemory(IList<SensorReading> sensors, string name)
        {
            return sensors.FirstOrDefault(s => s.Type == SensorType.SmallData && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && IsValid(s.Value) && s.Value >= 0 && s.Value < 1048576);
        }
        private void ReadCpuLoad(HardwareSnapshot snapshot)
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) { _hasCpuTimes = false; return; }
            long total = kernel - _kernel + user - _user;
            if (_hasCpuTimes && total > 0)
            {
                snapshot.CpuLoadPercent = Math.Max(0, Math.Min(100, (total - (idle - _idle)) * 100.0 / total));
                snapshot.HasCpuLoad = true;
            }
            _idle = idle; _kernel = kernel; _user = user; _hasCpuTimes = true;
        }
        private static void ReadMemory(HardwareSnapshot snapshot)
        {
            var memory = new MemoryStatus();
            if (!GlobalMemoryStatusEx(memory)) return;
            snapshot.HasRam = true; snapshot.RamLoadPercent = memory.Load;
            snapshot.RamTotalGb = memory.TotalPhys / (1024.0 * 1024 * 1024);
            snapshot.RamUsedGb = (memory.TotalPhys - memory.AvailablePhys) / (1024.0 * 1024 * 1024);
            snapshot.RamSubtitle = string.Format("{0:0.0} / {1:0.0} GB", snapshot.RamUsedGb, snapshot.RamTotalGb);
        }
        private void ReadNetwork(HardwareSnapshot snapshot)
        {
            long timestamp = Stopwatch.GetTimestamp();
            double seconds = _networkTimestamp == 0 ? 0 : (timestamp - _networkTimestamp) / (double)Stopwatch.Frequency;
            var current = new Dictionary<string, NetworkCounters>();
            var names = new List<string>();
            long received = 0, sent = 0;
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up ||
                        (network.NetworkInterfaceType != NetworkInterfaceType.Ethernet && network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)) continue;
                    try
                    {
                        var stats = network.GetIPv4Statistics();
                        var counters = new NetworkCounters { Received = stats.BytesReceived, Sent = stats.BytesSent };
                        current[network.Id] = counters; names.Add(network.Name);
                        NetworkCounters previous;
                        if (_networkCounters.TryGetValue(network.Id, out previous))
                        {
                            received += CounterDelta(previous.Received, counters.Received);
                            sent += CounterDelta(previous.Sent, counters.Sent);
                        }
                    }
                    catch (NetworkInformationException ex) { Diagnostics.Write("Lettura della scheda di rete fallita: " + network.Name, ex); }
                }
            }
            catch (NetworkInformationException ex) { Diagnostics.Write("Enumerazione delle schede di rete fallita", ex); }
            if (seconds > 0)
            {
                snapshot.DownloadSpeedKbps = received * 8.0 / (1000.0 * seconds);
                snapshot.UploadSpeedKbps = sent * 8.0 / (1000.0 * seconds);
            }
            _networkCounters.Clear();
            foreach (var item in current) _networkCounters[item.Key] = item.Value;
            _networkTimestamp = timestamp;
            snapshot.DownloadFormatted = FormatSpeed(snapshot.DownloadSpeedKbps);
            snapshot.UploadFormatted = FormatSpeed(snapshot.UploadSpeedKbps);
            snapshot.NetworkInterfaceName = names.Count == 0 ? "Nessuna connessione" : string.Join(" + ", names);
        }
        internal static long CounterDelta(long previous, long current) { return current >= previous ? current - previous : 0; }
        internal static string FormatSpeed(double kbps)
        {
            return kbps >= 1000 ? string.Format("{0:0.0} Mbps", kbps / 1000.0) : string.Format("{0:0.0} Kbps", kbps);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_computer != null)
            {
                try { _computer.Close(); }
                catch (Exception ex) { Diagnostics.Write("Chiusura dei sensori fallita", ex); }
            }
        }
    }
}
