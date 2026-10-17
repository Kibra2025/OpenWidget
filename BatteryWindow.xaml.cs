using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DeskHardwareWidget
{
    public partial class BatteryWindow : Window
    {
        private readonly MainWindow _host;
        private readonly Dictionary<string, BatteryCard> _cards = new Dictionary<string, BatteryCard>();
        private readonly List<BatteryCard> _emptySlots = new List<BatteryCard>();
        private HwndSource _source;
        private IntPtr _handle;
        private bool _allowClose;
        internal BatteryWindow(MainWindow host)
        {
            _host = host;
            InitializeComponent();
            for (int i = 0; i < 4; i++)
            {
                var slot = new BatteryCard(); slot.ShowEmptySlot();
                _emptySlots.Add(slot); BatteryCards.Children.Add(slot);
            }
        }
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_source != null) { ApplyOptions(); return; }
            _handle = new WindowInteropHelper(this).Handle;
            Win32Helper.MakeToolWindow(_handle);
            _source = HwndSource.FromHwnd(_handle);
            if (_source != null) _source.AddHook(WndProc);
            DevicesScroll.MaxHeight = Math.Max(176, Math.Min(528, WorkingAreas().Min(area => area.Height) - 100));
            var settings = _host.CurrentSettings;
            Left = settings.BatteryPositionSet ? settings.BatteryX : _host.Left;
            Top = settings.BatteryPositionSet ? settings.BatteryY : _host.Top + _host.ActualHeight + 8;
            ApplyOptions();
            RenderSnapshot(_host.LatestBatterySnapshot);
            ClampPosition();
        }
        internal void ApplyOptions()
        {
            if (_handle == IntPtr.Zero) return;
            var settings = _host.CurrentSettings;
            if (settings.BatteryPositionSet) { Left = settings.BatteryX; Top = settings.BatteryY; }
            Win32Helper.SetClickThrough(_handle, settings.BatteryClickThrough);
            RenderSnapshot(_host.LatestBatterySnapshot);
            ClampPosition();
        }
        internal static List<BatteryDeviceSnapshot> VisibleDevices(BatterySnapshot snapshot, WidgetSettings settings)
        {
            if (snapshot == null) return new List<BatteryDeviceSnapshot>();
            return snapshot.Devices.Where(device => device.IsPc ? settings.BatteryShowPc : settings.BatteryShowAccessories)
                .Where(device => device.IsConnected != false || settings.BatteryShowDisconnected)
                .Where(device => device.Percent.HasValue || settings.BatteryShowUnknown || device.IsConnected == false || device.IsReadingUnavailable)
                .ToList();
        }
        internal void RenderSnapshot(BatterySnapshot snapshot)
        {
            if (snapshot == null) return;
            var visible = VisibleDevices(snapshot, _host.CurrentSettings);
            // Keep cards (and UI identity) while they remain selected; remove
            // disconnected devices without retaining a misleading percentage.
            foreach (string id in _cards.Keys.Except(visible.Select(device => device.Id)).ToArray()) _cards.Remove(id);
            foreach (var device in visible)
            {
                BatteryCard card;
                if (!_cards.TryGetValue(device.Id, out card)) { card = new BatteryCard(); _cards.Add(device.Id, card); }
                card.UpdateBattery(device);
            }
            int slotCount = Math.Max(4, ((visible.Count + 3) / 4) * 4);
            var ordered = visible.Select(device => _cards[device.Id]).Concat(_emptySlots.Take(slotCount - visible.Count)).ToList();
            if (!BatteryCards.Children.OfType<BatteryCard>().SequenceEqual(ordered))
            {
                BatteryCards.Children.Clear();
                foreach (var card in ordered) BatteryCards.Children.Add(card);
            }
            WidgetSurface.ToolTip = snapshot.Note ?? (visible.Count == 0 ? "Nessuna batteria da mostrare. Gestisci i filtri con il tasto destro o dal menu Batterie." : null);
            UpdateLayout();
            if (_handle != IntPtr.Zero) { ClampPosition(); Win32Helper.PinToBottom(_handle); }
        }
        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == Win32Helper.WM_WINDOWPOSCHANGING)
            {
                var position = (Win32Helper.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(Win32Helper.WINDOWPOS));
                position.hwndInsertAfter = Win32Helper.HWND_BOTTOM;
                position.flags &= ~Win32Helper.SWP_NOZORDER;
                Marshal.StructureToPtr(position, lParam, false);
            }
            else if (message == Win32Helper.WM_ACTIVATE) Win32Helper.PinToBottom(hwnd);
            return IntPtr.Zero;
        }
        private List<Rect> WorkingAreas()
        {
            var transform = _source != null && _source.CompositionTarget != null ? _source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            return Forms.Screen.AllScreens.Select(screen => new Rect(transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top)),
                transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom)))).ToList();
        }
        internal void ClampPosition()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var position = WindowPlacement.Clamp(new Rect(Left, Top, ActualWidth, ActualHeight), WorkingAreas());
            Left = position.X; Top = position.Y;
        }
        internal void CenterOnDesktop()
        {
            var area = WorkingAreas().FirstOrDefault(rect => rect.Contains(_host.CenteredPosition));
            if (area.IsEmpty || area.Width <= 0) area = WorkingAreas()[0];
            Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
            ClampPosition(); _host.SaveBatteryPosition(Left, Top);
        }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var settings = _host.CurrentSettings;
            if (!settings.BatteryIsLocked && !settings.BatteryClickThrough && e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove(); ClampPosition(); _host.SaveBatteryPosition(Left, Top);
                Win32Helper.PinToBottom(_handle);
            }
        }
        private void ShowMenu()
        {
            var settings = _host.CurrentSettings;
            var menu = new ContextMenu { Resources = _host.Resources, PlacementTarget = this };
            AddMenu(menu, "Gestisci batterie…", _host.ShowBatterySettings);
            AddMenu(menu, (settings.BatteryClickThrough ? "✓ " : "") + "Click-through batterie", _host.ToggleBatteryClickThrough);
            AddMenu(menu, settings.BatteryIsLocked ? "Sblocca posizione batterie" : "Blocca posizione batterie", delegate
            {
                var next = _host.CurrentSettings; next.BatteryIsLocked = !next.BatteryIsLocked;
                if (!next.BatteryIsLocked) next.BatteryClickThrough = false;
                string error; _host.ApplySettings(next, out error);
            });
            AddMenu(menu, "Aggiorna batterie", _host.RefreshBatteries);
            AddMenu(menu, "Centra sul desktop", CenterOnDesktop);
            menu.Items.Add(new Separator());
            AddMenu(menu, "Nascondi widget batterie", delegate { _host.SetBatteryWidgetVisible(false); });
            menu.IsOpen = true;
        }
        private static void AddMenu(ContextMenu menu, string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }
        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) { e.Handled = true; ShowMenu(); }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            _host.SetBatteryWidgetVisible(false);
        }
        private void Window_Closed(object sender, EventArgs e) { if (_source != null) _source.RemoveHook(WndProc); }
        internal void CloseForShutdown() { _allowClose = true; Close(); }
    }
}
