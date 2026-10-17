using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeskHardwareWidget
{
    public partial class MainWindow : Window
    {
        private readonly WidgetSettings _settings;
        private readonly List<double> _cpuHistory = GraphRenderer.NewHistory();
        private readonly List<double> _ramHistory = GraphRenderer.NewHistory();
        private readonly List<double> _netHistory = GraphRenderer.NewHistory();
        private readonly List<GpuCard> _gpuCards = new List<GpuCard>();
        private CancellationTokenSource _cancellation;
        private Task _samplingTask;
        private Forms.NotifyIcon _trayIcon;
        private System.Drawing.Icon _ownedIcon;
        private HwndSource _source;
        private IntPtr _hwnd;
        private bool _closing;
        private bool _canClose;
        private bool _restarting;
        private int _refreshIntervalMs;
        private SettingsWindow _settingsWindow;
        private BatteryWindow _batteryWindow;
        private Task _batterySamplingTask;
        private int _batteryRefreshSeconds;
        private int _batteryRefreshRequested;
        internal event Action SettingsChanged;
        internal event Action<HardwareSnapshot> SnapshotRendered;
        internal event Action<BatterySnapshot> BatterySnapshotRendered;
        internal HardwareSnapshot LatestSnapshot { get; private set; }
        internal BatterySnapshot LatestBatterySnapshot { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            _settings = ConfigManager.Load();
            _refreshIntervalMs = _settings.RefreshIntervalMs;
            _batteryRefreshSeconds = _settings.BatteryRefreshSeconds;
        }
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.MakeToolWindow(_hwnd);
            _source = HwndSource.FromHwnd(_hwnd);
            if (_source != null) _source.AddHook(WndProc);
            Left = _settings.X; Top = _settings.Y;
            ApplyLockState(_settings.IsLocked);
            Win32Helper.SetClickThrough(_hwnd, _settings.IsClickThrough);
            ApplyCardsVisibility();
            ClampPosition();
            var app = (App)System.Windows.Application.Current;
            bool autostartFailed = false;
            if (!app.IsDiagnostic)
            {
                bool enable = _settings.AutostartWithWindows || app.StartedAutomatically || AutostartManager.HasLegacyAutostart();
                autostartFailed = !Win32Helper.SetAutostart(enable);
                _settings.AutostartWithWindows = autostartFailed ? Win32Helper.IsAutostartEnabled() : enable;
            }
            SaveSettings();
            if (!app.IsDiagnostic) InitTrayIcon();
            if (autostartFailed && _trayIcon != null)
                _trayIcon.ShowBalloonTip(5000, "OpenWidget", "Impossibile aggiornare l'avvio automatico come amministratore. Consulta widget.log.", Forms.ToolTipIcon.Warning);
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Win32Helper.PinToBottom(_hwnd);
            ApplyBatteryWindow();
            StartSampling();
        }
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32Helper.WM_WINDOWPOSCHANGING)
            {
                var position = (Win32Helper.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(Win32Helper.WINDOWPOS));
                position.hwndInsertAfter = Win32Helper.HWND_BOTTOM;
                position.flags &= ~Win32Helper.SWP_NOZORDER;
                Marshal.StructureToPtr(position, lParam, false);
            }
            else if (msg == Win32Helper.WM_ACTIVATE) Win32Helper.PinToBottom(hwnd);
            else if (msg == Win32Helper.WM_DEVICECHANGE && (wParam.ToInt64() == 7 || wParam.ToInt64() == 0x8000 || wParam.ToInt64() == 0x8004))
                RefreshBatteries();
            return IntPtr.Zero;
        }
        private void StartSampling()
        {
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            _samplingTask = Task.Factory.StartNew(delegate
            {
                try
                {
                    using (var monitor = new HardwareMonitor())
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var elapsed = Stopwatch.StartNew();
                            HardwareSnapshot snapshot;
                            try { snapshot = monitor.Update(); }
                            catch (Exception ex)
                            {
                                Diagnostics.Write("Aggiornamento dei sensori fallito", ex);
                                snapshot = new HardwareSnapshot { CpuSubtitle = "Errore sensori: vedi widget.log" };
                            }
                            if (token.IsCancellationRequested) break;
                            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { if (!_closing) RenderSnapshot(snapshot); }));
                            if (token.WaitHandle.WaitOne(Math.Max(50, Volatile.Read(ref _refreshIntervalMs) - (int)elapsed.ElapsedMilliseconds))) break;
                        }
                    }
                }
                catch (Exception ex) { Diagnostics.Write("Campionamento hardware interrotto", ex); }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _batterySamplingTask = Task.Factory.StartNew(delegate
            {
                var monitor = new BatteryMonitor();
                var sinceScan = Stopwatch.StartNew();
                bool first = true;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        bool refresh = first || Interlocked.Exchange(ref _batteryRefreshRequested, 0) != 0 ||
                            sinceScan.Elapsed.TotalSeconds >= Volatile.Read(ref _batteryRefreshSeconds);
                        var snapshot = monitor.Update(refresh, token);
                        if (refresh) { first = false; sinceScan.Restart(); }
                        if (token.IsCancellationRequested) break;
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { if (!_closing) RenderBatterySnapshot(snapshot); }));
                    }
                    catch (Exception ex) { Diagnostics.Write("Campionamento batterie fallito", ex); }
                    if (token.WaitHandle.WaitOne(5000)) break;
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        private async Task StopSampling()
        {
            if (_cancellation == null) return;
            _cancellation.Cancel();
            try { await Task.WhenAll(new[] { _samplingTask, _batterySamplingTask }.Where(task => task != null)); }
            catch (OperationCanceledException) { }
            _cancellation.Dispose(); _cancellation = null; _samplingTask = null; _batterySamplingTask = null;
        }
        internal void RenderBatterySnapshot(BatterySnapshot snapshot)
        {
            LatestBatterySnapshot = snapshot;
            if (_batteryWindow != null && _batteryWindow.IsVisible) _batteryWindow.RenderSnapshot(snapshot);
            if (BatterySnapshotRendered != null) BatterySnapshotRendered(snapshot);
        }
        internal void RenderSnapshot(HardwareSnapshot snapshot)
        {
            LatestSnapshot = snapshot;
            txtCpuPercent.Text = snapshot.HasCpuLoad ? string.Format("{0:0} %", snapshot.CpuLoadPercent) : "— %";
            txtCpuSubtitle.Text = snapshot.CpuSubtitle;
            txtCpuSubtitle.ToolTip = snapshot.HasCpuTemp ? "Temperatura rilevata dal sensore CPU" : snapshot.CpuSubtitle + ". Usa il menu dei sensori.";
            txtCpuName.Text = snapshot.CpuName; txtCpuName.ToolTip = snapshot.CpuName;
            GraphRenderer.Add(_cpuHistory, snapshot.HasCpuLoad ? snapshot.CpuLoadPercent : double.NaN);
            GraphRenderer.Draw(cpuGraphLine, cpuGraphFill, _cpuHistory, 100);
            var gpus = snapshot.Gpus.Count == 0 ? new List<GpuSnapshot> { HardwareMonitor.CreateGpuSnapshot("none", "Nessuna GPU rilevata", "GPU", new List<SensorReading>()) } : snapshot.Gpus;
            if (!_gpuCards.Select(card => card.DeviceId).SequenceEqual(gpus.Select(gpu => gpu.Id)))
            {
                var previous = _gpuCards.ToDictionary(card => card.DeviceId);
                foreach (var card in _gpuCards) CardsPanel.Children.Remove(card);
                _gpuCards.Clear();
                foreach (var gpu in gpus)
                {
                    GpuCard card;
                    if (!previous.TryGetValue(gpu.Id, out card))
                    {
                        card = new GpuCard(gpu.Id);
                        card.MenuRequested += delegate { ShowWpfContextMenu(); };
                    }
                    _gpuCards.Add(card);
                    CardsPanel.Children.Insert(_gpuCards.Count, card);
                }
                ApplyCardsVisibility();
                UpdateLayout();
                ClampPosition();
            }
            for (int i = 0; i < gpus.Count; i++) _gpuCards[i].UpdateGpu(gpus[i], i);
            txtRamPercent.Text = snapshot.HasRam ? string.Format("{0:0} %", snapshot.RamLoadPercent) : "— %";
            txtRamSubtitle.Text = snapshot.RamSubtitle;
            GraphRenderer.Add(_ramHistory, snapshot.HasRam ? snapshot.RamLoadPercent : double.NaN);
            GraphRenderer.Draw(ramGraphLine, ramGraphFill, _ramHistory, 100);
            txtNetDown.Text = "↓ " + snapshot.DownloadFormatted; txtNetUp.Text = "↑ " + snapshot.UploadFormatted;
            txtNetName.Text = snapshot.NetworkInterfaceName; txtNetName.ToolTip = snapshot.NetworkInterfaceName;
            GraphRenderer.Add(_netHistory, Math.Max(snapshot.DownloadSpeedKbps, snapshot.UploadSpeedKbps));
            double maximum = Math.Max(1000, _netHistory.Where(value => !double.IsNaN(value)).DefaultIfEmpty(0).Max());
            GraphRenderer.Draw(netGraphLine, netGraphFill, _netHistory, maximum);
            netGraphLine.ToolTip = "Scala rete: " + HardwareMonitor.FormatSpeed(maximum);
            if (_trayIcon != null)
            {
                string cpu = snapshot.HasCpuTemp ? string.Format("{0:0.0}°C", snapshot.CpuTempC) : "N/D";
                string text = "CPU " + cpu;
                var temperatures = gpus.Select((gpu, index) => new { Gpu = gpu, Index = index })
                    .Where(item => !_settings.HiddenGpuIds.Contains(item.Gpu.Id))
                    .Select(item => "GPU" + (item.Index + 1) + " " + (item.Gpu.HasTemperature ? string.Format("{0:0.0}°C", item.Gpu.TemperatureC) : "N/D"));
                if (temperatures.Any()) text += " | " + string.Join(" | ", temperatures);
                _trayIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            }
            if (_hwnd != IntPtr.Zero) Win32Helper.PinToBottom(_hwnd);
            if (SnapshotRendered != null) SnapshotRendered(snapshot);
        }
        private void InitTrayIcon()
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("/OpenWidget;component/Assets/App.ico", UriKind.Relative));
            using (var stream = resource.Stream)
            using (var icon = new System.Drawing.Icon(stream, 32, 32))
            {
                _ownedIcon = (System.Drawing.Icon)icon.Clone();
            }
            _trayIcon = new Forms.NotifyIcon { Icon = _ownedIcon, Text = "OpenWidget", Visible = true };
            var menu = new Forms.ContextMenuStrip { BackColor = System.Drawing.Color.FromArgb(28, 30, 38), ForeColor = System.Drawing.Color.White };
            menu.Opening += delegate
            {
                foreach (Forms.ToolStripItem item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) item.Dispose();
                menu.Items.Clear();
                foreach (var command in GetCommands())
                {
                    if (command == null) { menu.Items.Add(new Forms.ToolStripSeparator()); continue; }
                    var current = command;
                    var item = new Forms.ToolStripMenuItem(current.Label) { Checked = current.Checked };
                    item.Click += delegate { current.Execute(); };
                    menu.Items.Add(item);
                }
            };
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button == Forms.MouseButtons.Left) ShowSettings();
            };
            _trayIcon.MouseDoubleClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button == Forms.MouseButtons.Left) ShowSettings();
            };
        }
        internal sealed class WidgetCommand
        {
            public string Label; public bool Checked; public Action Execute;
            public WidgetCommand(string label, bool check, Action execute) { Label = label; Checked = check; Execute = execute; }
        }
        internal IList<WidgetCommand> GetCommands()
        {
            var commands = new List<WidgetCommand>
            {
                new WidgetCommand("Gestisci widget e impostazioni…", false, ShowSettings),
                new WidgetCommand("Gestisci batterie…", false, ShowBatterySettings),
                new WidgetCommand("Mostra widget batterie", _settings.BatteryWidgetEnabled, delegate { SetBatteryWidgetVisible(!_settings.BatteryWidgetEnabled); }),
                new WidgetCommand("Click-through widget batterie", _settings.BatteryClickThrough, ToggleBatteryClickThrough),
                null,
                new WidgetCommand(_settings.IsLocked ? "Sblocca posizione" : "Blocca posizione", _settings.IsLocked, ToggleLock),
                new WidgetCommand("Modalità click-through", _settings.IsClickThrough, ToggleClickThrough),
                new WidgetCommand("Avvia con Windows come amministratore", _settings.AutostartWithWindows, ToggleAutostart),
                null,
                new WidgetCommand("Modalità compatta (CPU e GPU selezionate)", !_settings.ShowMemoryCard && !_settings.ShowNetworkCard, ToggleCompact)
            };
            if (LatestSnapshot != null)
                for (int i = 0; i < LatestSnapshot.Gpus.Count; i++)
                {
                    var gpu = LatestSnapshot.Gpus[i];
                    commands.Add(new WidgetCommand("Mostra GPU " + (i + 1) + " · " + gpu.Name, !_settings.HiddenGpuIds.Contains(gpu.Id),
                        delegate { ToggleGpuVisibility(gpu.Id); }));
                }
            commands.AddRange(new List<WidgetCommand>
            {
                new WidgetCommand("Centra sul desktop principale", false, ResetPosition),
                null,
                new WidgetCommand(Win32Helper.IsPawnIoInstalled() ? "Configura / aggiorna PawnIO" : "Abilita temperature CPU (installa PawnIO)", false, InstallCpuDriver),
                new WidgetCommand("Riavvia come amministratore", false, delegate { Restart(true); }),
                new WidgetCommand("Rileva di nuovo i sensori", false, delegate { Restart(false); }),
                new WidgetCommand("Apri log diagnostico", false, OpenLog),
                null,
                new WidgetCommand("Esci", false, Close)
            });
            return commands;
        }
        private void ShowWpfContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, Resources = Resources };
            foreach (var command in GetCommands())
            {
                if (command == null) { menu.Items.Add(new System.Windows.Controls.Separator()); continue; }
                var current = command;
                var item = new System.Windows.Controls.MenuItem { Header = (current.Checked ? "✓ " : "") + current.Label };
                item.Click += delegate { current.Execute(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        private void ToggleLock()
        {
            ApplyLockState(!_settings.IsLocked);
            if (!_settings.IsLocked && _settings.IsClickThrough) { _settings.IsClickThrough = false; Win32Helper.SetClickThrough(_hwnd, false); }
            UpdateLayout(); ClampPosition(); SaveSettings();
        }
        private void ApplyLockState(bool locked) { _settings.IsLocked = locked; UnlockBanner.Visibility = locked ? Visibility.Collapsed : Visibility.Visible; }
        private void ToggleClickThrough()
        {
            _settings.IsClickThrough = !_settings.IsClickThrough;
            if (_settings.IsClickThrough) { ApplyLockState(true); UpdateLayout(); ClampPosition(); }
            Win32Helper.SetClickThrough(_hwnd, _settings.IsClickThrough); SaveSettings();
            if (_settings.IsClickThrough && _trayIcon != null) _trayIcon.ShowBalloonTip(3000, "OpenWidget", "Usa il menu dell'icona nell'area notifiche per disattivare il click-through.", Forms.ToolTipIcon.Info);
        }
        private void ToggleAutostart()
        {
            if (Win32Helper.SetAutostart(!_settings.AutostartWithWindows)) _settings.AutostartWithWindows = !_settings.AutostartWithWindows;
            else System.Windows.MessageBox.Show("Impossibile aggiornare l'avvio automatico. Consulta widget.log.", "OpenWidget");
            SaveSettings();
        }
        private void ToggleCompact()
        {
            bool show = !(_settings.ShowMemoryCard || _settings.ShowNetworkCard);
            _settings.ShowMemoryCard = show; _settings.ShowNetworkCard = show;
            ApplyCardsVisibility(); UpdateLayout(); ClampPosition(); SaveSettings();
        }
        private void ApplyCardsVisibility()
        {
            foreach (var card in _gpuCards)
                card.Visibility = _settings.HiddenGpuIds.Contains(card.DeviceId) ? Visibility.Collapsed : Visibility.Visible;
            cardMemory.Visibility = _settings.ShowMemoryCard ? Visibility.Visible : Visibility.Collapsed;
            cardNetwork.Visibility = _settings.ShowNetworkCard ? Visibility.Visible : Visibility.Collapsed;
            int count = 1 + _gpuCards.Count(card => card.Visibility == Visibility.Visible) + (_settings.ShowMemoryCard ? 1 : 0) + (_settings.ShowNetworkCard ? 1 : 0);
            cardNetwork.Width = count % 2 == 1 ? 512 : 252;
        }
        private void ToggleGpuVisibility(string deviceId)
        {
            if (_settings.HiddenGpuIds.Contains(deviceId)) _settings.HiddenGpuIds.Remove(deviceId);
            else _settings.HiddenGpuIds.Add(deviceId);
            ApplyCardsVisibility(); UpdateLayout(); ClampPosition(); SaveSettings();
        }
        private IList<Rect> WorkingAreas()
        {
            Matrix transform = _source != null && _source.CompositionTarget != null ? _source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            return Forms.Screen.AllScreens.Select(screen =>
            {
                var area = screen.WorkingArea;
                return new Rect(transform.Transform(new System.Windows.Point(area.Left, area.Top)), transform.Transform(new System.Windows.Point(area.Right, area.Bottom)));
            }).ToList();
        }
        private void ClampPosition()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var position = WindowPlacement.Clamp(new Rect(Left, Top, ActualWidth, ActualHeight), WorkingAreas());
            Left = position.X; Top = position.Y;
            _settings.X = Left; _settings.Y = Top;
        }
        internal void ResetPosition()
        {
            var position = CenteredPosition;
            Left = position.X; Top = position.Y;
            ClampPosition(); SaveSettings();
        }
        internal System.Windows.Point CenteredPosition
        {
            get { return CenterOnPrimary(ActualWidth, ActualHeight); }
        }
        internal System.Windows.Point CurrentBatteryPosition
        {
            get { return _batteryWindow != null && _batteryWindow.IsLoaded ? new System.Windows.Point(_batteryWindow.Left, _batteryWindow.Top) : new System.Windows.Point(_settings.BatteryX, _settings.BatteryY); }
        }
        internal System.Windows.Point CenteredBatteryPosition
        {
            get { return CenterOnPrimary(_batteryWindow != null && _batteryWindow.ActualWidth > 0 ? _batteryWindow.ActualWidth : 506,
                _batteryWindow != null && _batteryWindow.ActualHeight > 0 ? _batteryWindow.ActualHeight : 230); }
        }
        private System.Windows.Point CenterOnPrimary(double width, double height)
        {
            var screen = Forms.Screen.PrimaryScreen.WorkingArea;
            Matrix transform = _source != null && _source.CompositionTarget != null ? _source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var area = new Rect(transform.Transform(new System.Windows.Point(screen.Left, screen.Top)), transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom)));
            return new System.Windows.Point(area.Left + Math.Max(0, (area.Width - width) / 2), area.Top + Math.Max(0, (area.Height - height) / 2));
        }
        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!_closing) Dispatcher.BeginInvoke(new Action(delegate { if (!_closing) { ClampPosition(); if (_batteryWindow != null) _batteryWindow.ClampPosition(); SaveSettings(); } }));
        }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_settings.IsLocked && !_settings.IsClickThrough && e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove(); ClampPosition(); SaveSettings();
                Win32Helper.PinToBottom(_hwnd);
            }
        }
        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) { ShowWpfContextMenu(); e.Handled = true; }
        private void DotsMenu_MouseDown(object sender, MouseButtonEventArgs e) { ShowWpfContextMenu(); e.Handled = true; }
        private void BtnLockWidget_Click(object sender, RoutedEventArgs e) { ApplyLockState(true); UpdateLayout(); ClampPosition(); SaveSettings(); }
        private void SaveSettings()
        {
            ConfigManager.Save(_settings);
            if (SettingsChanged != null) SettingsChanged();
        }
        internal WidgetSettings CurrentSettings
        {
            get
            {
                return _settings.Copy();
            }
        }
        internal bool ApplySettings(WidgetSettings settings, out string error)
        {
            error = null;
            if (_closing || _restarting) { error = "Attendi il completamento dell'operazione in corso."; return false; }
            settings.Validate();
            if (settings.AutostartWithWindows != _settings.AutostartWithWindows && !Win32Helper.SetAutostart(settings.AutostartWithWindows))
            {
                error = "Impossibile aggiornare l'avvio automatico. Consulta il log diagnostico.";
                return false;
            }
            _settings.AutostartWithWindows = settings.AutostartWithWindows;
            _settings.IsClickThrough = settings.IsClickThrough;
            ApplyLockState(settings.IsLocked || settings.IsClickThrough);
            _settings.ShowMemoryCard = settings.ShowMemoryCard;
            _settings.ShowNetworkCard = settings.ShowNetworkCard;
            _settings.HiddenGpuIds = new List<string>(settings.HiddenGpuIds);
            _settings.RefreshIntervalMs = settings.RefreshIntervalMs;
            _settings.BatteryWidgetEnabled = settings.BatteryWidgetEnabled;
            _settings.BatteryShowPc = settings.BatteryShowPc;
            _settings.BatteryShowAccessories = settings.BatteryShowAccessories;
            _settings.BatteryShowDisconnected = settings.BatteryShowDisconnected;
            _settings.BatteryShowUnknown = settings.BatteryShowUnknown;
            _settings.BatteryIsLocked = settings.BatteryIsLocked;
            _settings.BatteryClickThrough = settings.BatteryClickThrough;
            _settings.BatteryRefreshSeconds = settings.BatteryRefreshSeconds;
            _settings.BatteryX = settings.BatteryX; _settings.BatteryY = settings.BatteryY;
            _settings.BatteryPositionSet = settings.BatteryPositionSet;
            Volatile.Write(ref _batteryRefreshSeconds, settings.BatteryRefreshSeconds);
            Volatile.Write(ref _refreshIntervalMs, settings.RefreshIntervalMs);
            if (_hwnd != IntPtr.Zero) Win32Helper.SetClickThrough(_hwnd, _settings.IsClickThrough);
            ApplyCardsVisibility(); UpdateLayout();
            Left = settings.X; Top = settings.Y;
            _settings.X = Left; _settings.Y = Top;
            ClampPosition(); ApplyBatteryWindow(); SaveSettings();
            return true;
        }
        private void ApplyBatteryWindow()
        {
            if (_closing) return;
            if (!_settings.BatteryWidgetEnabled)
            {
                if (_batteryWindow != null) { _batteryWindow.ApplyOptions(); _batteryWindow.Hide(); CaptureBatteryPosition(); }
                return;
            }
            if (_batteryWindow == null)
            {
                _batteryWindow = new BatteryWindow(this);
                if (((App)System.Windows.Application.Current).IsDiagnostic) _batteryWindow.Opacity = 0;
            }
            if (!_batteryWindow.IsVisible) _batteryWindow.Show();
            _batteryWindow.ApplyOptions();
            CaptureBatteryPosition();
        }
        private void CaptureBatteryPosition()
        {
            if (_settings.BatteryPositionSet && _batteryWindow != null && _batteryWindow.IsLoaded)
            {
                _settings.BatteryX = _batteryWindow.Left; _settings.BatteryY = _batteryWindow.Top;
            }
        }
        internal void SaveBatteryPosition(double x, double y)
        {
            _settings.BatteryX = x; _settings.BatteryY = y; _settings.BatteryPositionSet = true;
            SaveSettings();
        }
        internal void SetBatteryWidgetVisible(bool visible)
        {
            _settings.BatteryWidgetEnabled = visible;
            ApplyBatteryWindow(); SaveSettings();
        }
        internal void RefreshBatteries() { Interlocked.Exchange(ref _batteryRefreshRequested, 1); }
        internal void ToggleBatteryClickThrough()
        {
            if (_closing || _restarting) return;
            var settings = CurrentSettings;
            settings.BatteryClickThrough = !settings.BatteryClickThrough;
            if (settings.BatteryClickThrough) settings.BatteryIsLocked = true;
            string error;
            ApplySettings(settings, out error);
        }
        internal void CenterBatteryWidget()
        {
            SetBatteryWidgetVisible(true);
            if (_batteryWindow != null) _batteryWindow.CenterOnDesktop();
        }
        internal void ShowBatterySettings()
        {
            ShowSettings();
            if (_settingsWindow != null) _settingsWindow.SelectSection("Batteries");
        }
        internal void ShowSettings()
        {
            if (_closing || _restarting) return;
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += delegate { _settingsWindow = null; };
                _settingsWindow.Show();
            }
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
        internal void OpenLog()
        {
            try
            {
                if (!System.IO.File.Exists(AppPaths.LogPath)) Diagnostics.Write("Log diagnostico avviato", null);
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppPaths.LogPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Diagnostics.Write("Impossibile aprire il log", ex); }
        }
        internal async void InstallCpuDriver()
        {
            if (_restarting || _closing) return;
            string setup = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "PawnIO_setup.exe");
            if (!System.IO.File.Exists(setup)) { System.Windows.MessageBox.Show("PawnIO_setup.exe non trovato nella cartella tools del pacchetto portatile.", "OpenWidget"); return; }
            _restarting = true;
            try
            {
                using (var process = Process.Start(new ProcessStartInfo(setup, "-install") { UseShellExecute = true, Verb = "runas" }))
                {
                    await Task.Run(delegate { process.WaitForExit(); });
                    if (process.ExitCode != 0) { System.Windows.MessageBox.Show("Installazione non completata. Le altre metriche restano disponibili.", "OpenWidget"); return; }
                }
                if (_closing) return;
                _restarting = false;
                Restart(true);
            }
            catch (Win32Exception ex) { if (ex.NativeErrorCode != 1223) Diagnostics.Write("Installazione PawnIO fallita", ex); }
            finally { _restarting = false; }
        }
        internal async void Restart(bool administrator)
        {
            if (_restarting || _closing) return;
            _restarting = true;
            SaveSettings(); await StopSampling();
            var app = (App)System.Windows.Application.Current;
            app.ReleaseInstance();
            try
            {
                Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location) { UseShellExecute = true, Verb = administrator ? "runas" : "" });
                Close();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) Diagnostics.Write("Riavvio fallito", ex);
                app.AcquireInstance(); StartSampling();
            }
            finally { _restarting = false; }
        }
        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_canClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            if (_settingsWindow != null) _settingsWindow.Close();
            if (_batteryWindow != null) _batteryWindow.CloseForShutdown();
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            SaveSettings();
            await StopSampling();
            if (_source != null) _source.RemoveHook(WndProc);
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.ContextMenuStrip.Dispose(); _trayIcon.Dispose(); }
            if (_ownedIcon != null) _ownedIcon.Dispose();
            _canClose = true;
            await Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
