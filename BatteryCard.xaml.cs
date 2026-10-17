using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskHardwareWidget
{
    public partial class BatteryCard : UserControl
    {
        private static readonly Dictionary<string, string> Icons = new Dictionary<string, string>
        {
            { "Laptop", "M3,3 H21 V17 H3 Z M1,20 H23 L21,17 H3 Z" },
            { "Mouse", "M12,2 C7,2 5,5 5,9 V15 C5,24 19,24 19,15 V9 C19,5 17,2 12,2 Z M12,2 V10 M5,10 H19 M12,5 V7" },
            { "Headphones", "M3,14 V10 C3,-1 21,-1 21,10 V14 M3,11 H7 V21 H3 Z M17,11 H21 V21 H17 Z" },
            { "Keyboard", "M1,5 H23 V19 H1 Z M5,9 H6 M10,9 H11 M15,9 H16 M20,9 H21 M5,13 H6 M10,13 H11 M15,13 H16 M5,16 H19" },
            { "Phone", "M6,1 H18 V23 H6 Z M10,4 H14 M11,20 H13" },
            { "Watch", "M8,1 H16 L17,6 H7 Z M7,18 H17 L16,23 H8 Z M6,6 H18 V18 H6 Z" },
            { "Gamepad", "M7,5 H17 C22,5 25,22 20,22 L15,17 H9 L4,22 C-1,22 2,5 7,5 Z M5,11 H11 M8,8 V14 M16,10 H17 M19,13 H20" },
            { "Device", "M2,6 H20 V20 H2 Z M20,10 H23 V16 H20 M6,10 H8 V16 H6 Z M12,10 H14 V16 H12 Z" }
        };
        internal bool IsPlaceholder { get; private set; }
        public BatteryCard() { InitializeComponent(); }
        internal void ShowEmptySlot()
        {
            IsPlaceholder = true;
            RingTrack.StrokeDashArray = null;
            RingProgress.Data = Geometry.Empty;
            FullRing.Visibility = ChargingBadge.Visibility = AcBadge.Visibility = DeviceIcon.Visibility = DeviceName.Visibility = PercentText.Visibility = PowerStateText.Visibility = Visibility.Collapsed;
            ToolTip = "Spazio libero · nessun dispositivo da mostrare";
            System.Windows.Automation.AutomationProperties.SetName(this, "Spazio libero per un dispositivo");
        }
        internal void UpdateBattery(BatteryDeviceSnapshot device)
        {
            IsPlaceholder = false;
            DeviceIcon.Visibility = DeviceName.Visibility = PercentText.Visibility = PowerStateText.Visibility = Visibility.Visible;
            int? level = device.IsConnected == false ? null : BatteryMonitor.ValidPercent(device.Percent);
            var accent = new SolidColorBrush(level.HasValue && level <= 10 ? Color.FromRgb(251, 113, 133) :
                level.HasValue && level <= 20 ? Color.FromRgb(251, 191, 36) :
                device.IsCharging == true ? Color.FromRgb(74, 222, 128) : device.IsPc ? Color.FromRgb(56, 189, 248) : Color.FromRgb(74, 222, 128));
            RingProgress.Stroke = FullRing.Stroke = accent;
            RingProgress.Data = CreateArc(level);
            FullRing.Visibility = level == 100 ? Visibility.Visible : Visibility.Collapsed;
            RingTrack.StrokeDashArray = level.HasValue ? null : new DoubleCollection { 1, 2 };
            PercentText.Text = level.HasValue ? level.Value + " %" : "—";
            DeviceName.Text = device.Name;
            DeviceName.ToolTip = device.Name;
            PowerStateText.Text = device.IsPc && device.IsOnAc == true && device.IsCharging == false && device.Percent != 100 ? "Collegato · non in carica" :
                !device.IsCharging.HasValue && !device.IsPc && device.IsConnected != false && string.IsNullOrEmpty(device.BatteryStatusText) ? "Ricarica N/D" : device.StatusText;
            if (device.Kind == "Gamepad" && device.Connection == "USB" && device.IsConnected != false)
                PowerStateText.Text = "USB · " + (!level.HasValue && !device.IsCharging.HasValue ? "batteria N/D" : PowerStateText.Text);
            string icon;
            DeviceIcon.Data = Geometry.Parse(Icons.TryGetValue(device.Kind ?? "Device", out icon) ? icon : Icons["Device"]);
            ChargingBadge.Visibility = device.IsCharging == true && device.IsConnected != false ? Visibility.Visible : Visibility.Collapsed;
            AcBadge.Visibility = device.IsPc && device.IsOnAc == true && device.IsCharging != true ? Visibility.Visible : Visibility.Collapsed;
            string detail = device.Name + Environment.NewLine + (level.HasValue ? level + " % · " : "Carica non disponibile · ") + device.StatusText;
            if (!device.IsCharging.HasValue && !device.IsPc) detail += Environment.NewLine + "Stato di ricarica non comunicato dal dispositivo.";
            if (device.RemainingSeconds.HasValue)
            {
                var remaining = TimeSpan.FromSeconds(device.RemainingSeconds.Value);
                detail += Environment.NewLine + string.Format("Autonomia stimata: {0} h {1:00} min", (int)remaining.TotalHours, remaining.Minutes);
            }
            if (device.IsLogitechHid) detail += Environment.NewLine + device.Connection + " · lettura diretta Logitech HID++";
            else if (device.IsDualSenseHid)
            {
                detail += Environment.NewLine + device.Connection + " · lettura diretta DualSense HID";
                if (level.HasValue) detail += Environment.NewLine + "Livello indicativo: il controller comunica la carica a intervalli del 10%.";
                if (device.IsOnAc == true) detail += Environment.NewLine + "Alimentazione USB collegata";
            }
            else if (device.IsBluetooth) detail += Environment.NewLine + "Bluetooth · dato fornito da Windows";
            else if (!string.IsNullOrEmpty(device.Connection)) detail += Environment.NewLine + device.Connection + " · dispositivo rilevato da Windows";
            if (device.ReportedAtUtc.HasValue) detail += Environment.NewLine + (device.IsLogitechHid || device.IsDualSenseHid ? "Lettura dal dispositivo: " : "Timestamp riportato da Windows: ") + device.ReportedAtUtc.Value.ToLocalTime().ToString("g");
            ToolTip = detail;
            System.Windows.Automation.AutomationProperties.SetName(this, detail);
        }
        internal static Geometry CreateArc(int? level)
        {
            if (!level.HasValue || level <= 0 || level >= 100) return Geometry.Empty;
            double angle = level.Value * Math.PI / 50;
            var figure = new PathFigure { StartPoint = new Point(44, 7.5), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(44 + 36.5 * Math.Sin(angle), 44 - 36.5 * Math.Cos(angle)),
                new Size(36.5, 36.5), 0, level > 50, SweepDirection.Clockwise, true));
            return new PathGeometry(new[] { figure });
        }
    }
}
