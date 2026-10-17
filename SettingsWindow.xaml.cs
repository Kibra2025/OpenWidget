using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskHardwareWidget
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _widget;
        private bool _loading = true;
        private bool _dirty;
        private Point _loadedBatteryPosition;
        private HashSet<string> _pendingHiddenGpuIds = new HashSet<string>(StringComparer.Ordinal);
        private List<GpuVisibilityOption> _gpuOptions = new List<GpuVisibilityOption>();

        internal sealed class GpuVisibilityOption : INotifyPropertyChanged
        {
            private bool _isVisible;
            private readonly Action<GpuVisibilityOption> _changed;
            public string Id { get; private set; }
            public string Label { get; private set; }
            public string Name { get; private set; }
            public string AccessibleName { get { return "Mostra " + Label + " · " + Name; } }
            public event PropertyChangedEventHandler PropertyChanged;
            public bool IsVisible
            {
                get { return _isVisible; }
                set
                {
                    if (_isVisible == value) return;
                    _isVisible = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("IsVisible"));
                    _changed(this);
                }
            }
            public GpuVisibilityOption(GpuSnapshot gpu, int index, bool visible, Action<GpuVisibilityOption> changed)
            {
                Id = gpu.Id; Name = gpu.Name; Label = "GPU " + (index + 1); _isVisible = visible; _changed = changed;
            }
        }

        internal SettingsWindow(MainWindow widget)
        {
            _widget = widget;
            InitializeComponent();
            LogPath.Text = AppPaths.LogPath;
            bool driverInstalled = Win32Helper.IsPawnIoInstalled();
            DriverStatus.Text = driverInstalled ? "PawnIO installato · " : "PawnIO richiesto per la lettura dei sensori CPU · ";
            DriverStatus.Text += Win32Helper.IsRunningAsAdministrator() ? "Esecuzione come amministratore" : "Esecuzione senza privilegi di amministratore";
            DriverButton.Content = driverInstalled ? "Configura / aggiorna PawnIO" : "Installa PawnIO per le temperature CPU";
            LoadSettings();
            RefreshHardware(_widget.LatestSnapshot);
            NavCards.IsChecked = true;
            _widget.SettingsChanged += Widget_SettingsChanged;
            _widget.SnapshotRendered += RefreshHardware;
            _widget.BatterySnapshotRendered += RefreshBatteryDevices;
            RefreshBatteryDevices(_widget.LatestBatterySnapshot);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Keep the panel reachable on small screens and with display scaling.
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _widget.SettingsChanged -= Widget_SettingsChanged;
            _widget.SnapshotRendered -= RefreshHardware;
            _widget.BatterySnapshotRendered -= RefreshBatteryDevices;
        }

        private void Widget_SettingsChanged()
        {
            if (!_dirty) LoadSettings();
        }

        private void LoadSettings()
        {
            _loading = true;
            var settings = _widget.CurrentSettings;
            _pendingHiddenGpuIds = new HashSet<string>(settings.HiddenGpuIds, StringComparer.Ordinal);
            RefreshGpuOptions(_widget.LatestSnapshot);
            ShowMemory.IsChecked = settings.ShowMemoryCard;
            ShowNetwork.IsChecked = settings.ShowNetworkCard;
            PositionX.Text = settings.X.ToString("0.##", CultureInfo.CurrentCulture);
            PositionY.Text = settings.Y.ToString("0.##", CultureInfo.CurrentCulture);
            _loadedBatteryPosition = _widget.CurrentBatteryPosition;
            BatteryPositionX.Text = _loadedBatteryPosition.X.ToString("0.##", CultureInfo.CurrentCulture);
            BatteryPositionY.Text = _loadedBatteryPosition.Y.ToString("0.##", CultureInfo.CurrentCulture);
            LockPosition.IsChecked = settings.IsLocked;
            ClickThrough.IsChecked = settings.IsClickThrough;
            Autostart.IsChecked = settings.AutostartWithWindows;
            RefreshInterval.Text = settings.RefreshIntervalMs.ToString(CultureInfo.CurrentCulture);
            ShowBatteryWidget.IsChecked = settings.BatteryWidgetEnabled;
            ShowPcBattery.IsChecked = settings.BatteryShowPc;
            ShowAccessoryBatteries.IsChecked = settings.BatteryShowAccessories;
            ShowUnknownBattery.IsChecked = settings.BatteryShowUnknown;
            ShowDisconnectedBattery.IsChecked = settings.BatteryShowDisconnected;
            LockBattery.IsChecked = settings.BatteryIsLocked;
            BatteryClickThrough.IsChecked = settings.BatteryClickThrough;
            BatteryRefreshInterval.Text = settings.BatteryRefreshSeconds.ToString(CultureInfo.CurrentCulture);
            _loading = false;
            SetDirty(false);
        }

        private void SetDirty(bool value)
        {
            _dirty = value;
            ApplyButton.IsEnabled = value;
            RevertButton.IsEnabled = value;
            SaveStatus.Text = value ? "Modifiche da applicare" : "Tutte le modifiche sono salvate";
            SaveStatus.Foreground = new SolidColorBrush(value ? Color.FromRgb(251, 191, 36) : Color.FromRgb(148, 163, 184));
        }

        private void Control_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loading) SetDirty(true);
        }

        private void ClickThrough_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            LockPosition.IsChecked = true;
            SetDirty(true);
        }

        private void LockPosition_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ClickThrough.IsChecked = false;
            SetDirty(true);
        }

        private void Navigation_Checked(object sender, RoutedEventArgs e)
        {
            if (CardsPage == null) return;
            string section = (string)((RadioButton)sender).Tag;
            CardsPage.Visibility = section == "Cards" ? Visibility.Visible : Visibility.Collapsed;
            PositionPage.Visibility = section == "Position" ? Visibility.Visible : Visibility.Collapsed;
            GeneralPage.Visibility = section == "General" ? Visibility.Visible : Visibility.Collapsed;
            DiagnosticsPage.Visibility = section == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
            BatteriesPage.Visibility = section == "Batteries" ? Visibility.Visible : Visibility.Collapsed;
            PageScroll.ScrollToTop();
        }
        internal void SelectSection(string section)
        {
            foreach (var navigation in LogicalTreeNavigation())
                if ((string)navigation.Tag == section) navigation.IsChecked = true;
        }
        private void RefreshBatteryDevices(BatterySnapshot snapshot)
        {
            if (snapshot == null) return;
            BatteryDevicesSummary.Text = snapshot.Devices.Count == 0 ? "Nessuna batteria rilevata." :
                string.Join(Environment.NewLine, snapshot.Devices.Select(device => device.Name + " · " +
                    (device.Percent.HasValue ? device.Percent.Value + " %" : "Carica N/D") + " · " + device.StatusText));
            if (snapshot.Note != null) BatteryDevicesSummary.Text += Environment.NewLine + snapshot.Note;
        }
        private void BatteryClickThrough_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            LockBattery.IsChecked = true; SetDirty(true);
        }
        private void LockBattery_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            BatteryClickThrough.IsChecked = false; SetDirty(true);
        }
        private void CenterBattery_Click(object sender, RoutedEventArgs e)
        {
            var position = _widget.CenteredBatteryPosition;
            BatteryPositionX.Text = position.X.ToString("0.##", CultureInfo.CurrentCulture);
            BatteryPositionY.Text = position.Y.ToString("0.##", CultureInfo.CurrentCulture);
        }
        private void RefreshBatteries_Click(object sender, RoutedEventArgs e) { _widget.RefreshBatteries(); }

        private void RefreshHardware(HardwareSnapshot snapshot)
        {
            if (snapshot == null) return;
            CpuName.Text = string.IsNullOrWhiteSpace(snapshot.CpuName) ? "Processore" : snapshot.CpuName;
            RefreshGpuOptions(snapshot);
            CpuSensorStatus.Text = snapshot.CpuSubtitle;
        }

        private void RefreshGpuOptions(HardwareSnapshot snapshot)
        {
            var gpus = snapshot == null ? new List<GpuSnapshot>() : snapshot.Gpus;
            if (!_gpuOptions.Select(option => option.Id).SequenceEqual(gpus.Select(gpu => gpu.Id)) ||
                !_gpuOptions.Select(option => option.Name).SequenceEqual(gpus.Select(gpu => gpu.Name)))
            {
                _gpuOptions = gpus.Select((gpu, index) => new GpuVisibilityOption(gpu, index, !_pendingHiddenGpuIds.Contains(gpu.Id), GpuVisibility_Changed)).ToList();
                GpuOptions.ItemsSource = _gpuOptions;
            }
            else if (_loading)
                foreach (var option in _gpuOptions) option.IsVisible = !_pendingHiddenGpuIds.Contains(option.Id);
            GpuEmpty.Visibility = gpus.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowAllGpusButton.IsEnabled = HideAllGpusButton.IsEnabled = gpus.Count > 0;
            UpdateGpuSummary();
        }

        private void GpuVisibility_Changed(GpuVisibilityOption option)
        {
            if (_loading) return;
            if (option.IsVisible) _pendingHiddenGpuIds.Remove(option.Id);
            else _pendingHiddenGpuIds.Add(option.Id);
            UpdateGpuSummary();
            SetDirty(true);
        }

        private void UpdateGpuSummary()
        {
            GpuSummary.Text = _gpuOptions.Count == 0 ? "Nessuna rilevata" : _gpuOptions.Count(option => option.IsVisible) + " di " + _gpuOptions.Count + " visibili";
        }

        private void ShowAllGpus_Click(object sender, RoutedEventArgs e)
        {
            _pendingHiddenGpuIds.Clear();
            foreach (var option in _gpuOptions) option.IsVisible = true;
            SetDirty(true);
        }

        private void HideAllGpus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var option in _gpuOptions) option.IsVisible = false;
        }

        private static bool TryCoordinate(string text, out double coordinate)
        {
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out coordinate) ||
                          double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate);
            return parsed && !double.IsNaN(coordinate) && !double.IsInfinity(coordinate);
        }

        internal bool ApplyChanges()
        {
            double x, y, batteryX, batteryY;
            int interval;
            if (!TryCoordinate(PositionX.Text, out x))
            {
                ShowValidation("Inserisci un numero valido per la coordinata X.", "Position", PositionX);
                return false;
            }
            if (!TryCoordinate(PositionY.Text, out y))
            {
                ShowValidation("Inserisci un numero valido per la coordinata Y.", "Position", PositionY);
                return false;
            }
            if (!TryCoordinate(BatteryPositionX.Text, out batteryX))
            {
                ShowValidation("Inserisci un numero valido per la coordinata X delle batterie.", "Position", BatteryPositionX);
                return false;
            }
            if (!TryCoordinate(BatteryPositionY.Text, out batteryY))
            {
                ShowValidation("Inserisci un numero valido per la coordinata Y delle batterie.", "Position", BatteryPositionY);
                return false;
            }
            if (!int.TryParse(RefreshInterval.Text, out interval) || interval < 250 || interval > 60000)
            {
                ShowValidation("L'intervallo deve essere un numero intero tra 250 e 60.000 ms.", "General", RefreshInterval);
                return false;
            }
            int batteryInterval;
            if (!int.TryParse(BatteryRefreshInterval.Text, out batteryInterval) || batteryInterval < 10 || batteryInterval > 300)
            {
                ShowValidation("L'intervallo degli accessori deve essere tra 10 e 300 secondi.", "Batteries", BatteryRefreshInterval);
                return false;
            }
            var settings = _widget.CurrentSettings;
            settings.X = x; settings.Y = y; settings.RefreshIntervalMs = interval;
            settings.IsLocked = LockPosition.IsChecked == true; settings.IsClickThrough = ClickThrough.IsChecked == true;
            settings.AutostartWithWindows = Autostart.IsChecked == true;
            settings.ShowMemoryCard = ShowMemory.IsChecked == true; settings.ShowNetworkCard = ShowNetwork.IsChecked == true;
            settings.HiddenGpuIds = _pendingHiddenGpuIds.ToList();
            settings.BatteryWidgetEnabled = ShowBatteryWidget.IsChecked == true;
            settings.BatteryShowPc = ShowPcBattery.IsChecked == true; settings.BatteryShowAccessories = ShowAccessoryBatteries.IsChecked == true;
            settings.BatteryShowUnknown = ShowUnknownBattery.IsChecked == true; settings.BatteryShowDisconnected = ShowDisconnectedBattery.IsChecked == true;
            settings.BatteryIsLocked = LockBattery.IsChecked == true; settings.BatteryClickThrough = BatteryClickThrough.IsChecked == true;
            settings.BatteryRefreshSeconds = batteryInterval;
            settings.BatteryX = batteryX; settings.BatteryY = batteryY;
            settings.BatteryPositionSet = settings.BatteryPositionSet || Math.Abs(batteryX - _loadedBatteryPosition.X) > 0.005 || Math.Abs(batteryY - _loadedBatteryPosition.Y) > 0.005;
            string error;
            if (!_widget.ApplySettings(settings, out error))
            {
                SaveStatus.Text = error;
                SaveStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                return false;
            }
            LoadSettings();
            return true;
        }

        private void ShowValidation(string message, string section, TextBox input)
        {
            foreach (var navigation in LogicalTreeNavigation())
                if ((string)navigation.Tag == section) navigation.IsChecked = true;
            SaveStatus.Text = message;
            SaveStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            input.Focus(); input.SelectAll();
        }

        private System.Collections.Generic.IEnumerable<RadioButton> LogicalTreeNavigation()
        {
            var panel = (Panel)NavCards.Parent;
            return panel.Children.OfType<RadioButton>();
        }

        private void Apply_Click(object sender, RoutedEventArgs e) { ApplyChanges(); }
        private void Revert_Click(object sender, RoutedEventArgs e) { LoadSettings(); }
        private void Compact_Click(object sender, RoutedEventArgs e) { ShowMemory.IsChecked = false; ShowNetwork.IsChecked = false; }
        private void Center_Click(object sender, RoutedEventArgs e)
        {
            var position = _widget.CenteredPosition;
            PositionX.Text = position.X.ToString("0.##", CultureInfo.CurrentCulture);
            PositionY.Text = position.Y.ToString("0.##", CultureInfo.CurrentCulture);
        }

        private bool ApplyPendingChanges() { return !_dirty || ApplyChanges(); }
        private void Driver_Click(object sender, RoutedEventArgs e) { if (ApplyPendingChanges()) _widget.InstallCpuDriver(); }
        private void Rescan_Click(object sender, RoutedEventArgs e) { if (ApplyPendingChanges()) _widget.Restart(false); }
        private void Restart_Click(object sender, RoutedEventArgs e) { if (ApplyPendingChanges()) _widget.Restart(true); }
        private void Log_Click(object sender, RoutedEventArgs e) { _widget.OpenLog(); }
        private void Exit_Click(object sender, RoutedEventArgs e) { if (ApplyPendingChanges()) _widget.Close(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
    }
}
