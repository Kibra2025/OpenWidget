using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DeskHardwareWidget
{
    [DataContract]
    public class WidgetSettings
    {
        [DataMember] public double X { get; set; }
        [DataMember] public double Y { get; set; }
        [DataMember] public bool IsLocked { get; set; }
        [DataMember] public bool IsClickThrough { get; set; }
        [DataMember] public bool AutostartWithWindows { get; set; }
        [DataMember] public int RefreshIntervalMs { get; set; }
        [DataMember] public bool ShowMemoryCard { get; set; }
        [DataMember] public bool ShowNetworkCard { get; set; }
        [DataMember] public List<string> HiddenGpuIds { get; set; }
        [DataMember] public bool BatteryWidgetEnabled { get; set; }
        [DataMember] public bool BatteryShowPc { get; set; }
        [DataMember] public bool BatteryShowAccessories { get; set; }
        [DataMember] public bool BatteryShowDisconnected { get; set; }
        [DataMember] public bool BatteryShowUnknown { get; set; }
        [DataMember] public bool BatteryIsLocked { get; set; }
        [DataMember] public bool BatteryClickThrough { get; set; }
        [DataMember] public double BatteryX { get; set; }
        [DataMember] public double BatteryY { get; set; }
        [DataMember] public bool BatteryPositionSet { get; set; }
        [DataMember] public int BatteryRefreshSeconds { get; set; }

        public WidgetSettings() { SetDefaults(); }
        [OnDeserializing] private void OnDeserializing(StreamingContext context) { SetDefaults(); }
        private void SetDefaults()
        {
            X = 80; Y = 80; IsLocked = false; IsClickThrough = false;
            AutostartWithWindows = false; RefreshIntervalMs = 1000;
            ShowMemoryCard = true; ShowNetworkCard = true;
            HiddenGpuIds = new List<string>();
            BatteryWidgetEnabled = true; BatteryShowPc = true; BatteryShowAccessories = true;
            BatteryShowDisconnected = false; BatteryShowUnknown = true; BatteryIsLocked = false; BatteryClickThrough = false;
            BatteryX = 80; BatteryY = 540; BatteryPositionSet = false; BatteryRefreshSeconds = 30;
        }
        public void Validate()
        {
            if (double.IsNaN(X) || double.IsInfinity(X)) X = 80;
            if (double.IsNaN(Y) || double.IsInfinity(Y)) Y = 80;
            if (RefreshIntervalMs <= 0) RefreshIntervalMs = 1000;
            RefreshIntervalMs = Math.Max(250, Math.Min(60000, RefreshIntervalMs));
            HiddenGpuIds = (HiddenGpuIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            if (double.IsNaN(BatteryX) || double.IsInfinity(BatteryX)) BatteryX = 80;
            if (double.IsNaN(BatteryY) || double.IsInfinity(BatteryY)) BatteryY = 540;
            if (BatteryRefreshSeconds <= 0) BatteryRefreshSeconds = 30;
            BatteryRefreshSeconds = Math.Max(10, Math.Min(300, BatteryRefreshSeconds));
            if (BatteryClickThrough) BatteryIsLocked = true;
        }
        internal WidgetSettings Copy()
        {
            var copy = (WidgetSettings)MemberwiseClone();
            copy.HiddenGpuIds = new List<string>(HiddenGpuIds);
            return copy;
        }
    }

    public static class ConfigManager
    {
        private static readonly object Sync = new object();
        public static WidgetSettings Load()
        {
            lock (Sync)
            {
                WidgetSettings settings = Read(AppPaths.ConfigPath);
                if (settings == null) settings = Read(AppPaths.ConfigPath + ".bak");
                if (settings == null) settings = new WidgetSettings();
                settings.Validate();
                return settings;
            }
        }
        private static WidgetSettings Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (WidgetSettings)new DataContractJsonSerializer(typeof(WidgetSettings)).ReadObject(stream);
            }
            catch (Exception ex) { Diagnostics.Write("Impossibile leggere la configurazione: " + path, ex); return null; }
        }
        public static void Save(WidgetSettings settings)
        {
            lock (Sync)
            {
                settings.Validate();
                string temporary = AppPaths.ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(WidgetSettings)).WriteObject(stream, settings);
                        stream.Flush(true);
                    }
                    if (File.Exists(AppPaths.ConfigPath)) File.Replace(temporary, AppPaths.ConfigPath, AppPaths.ConfigPath + ".bak");
                    else File.Move(temporary, AppPaths.ConfigPath);
                }
                catch (Exception ex) { Diagnostics.Write("Impossibile salvare la configurazione", ex); }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
