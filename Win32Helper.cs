using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace DeskHardwareWidget
{
    public static class Win32Helper
    {
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;

        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_ACTIVATE = 0x0006;
        public const int WM_DEVICECHANGE = 0x0219;
        public const int WM_SETFOCUS = 0x0007;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hWnd, nIndex);
            else
                return GetWindowLong32(hWnd, nIndex);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            else
                return SetWindowLong32(hWnd, nIndex, dwNewLong);
        }

        public static void MakeToolWindow(IntPtr hWnd)
        {
            IntPtr style = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            long newStyle = style.ToInt64() | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(newStyle));
        }

        public static void SetClickThrough(IntPtr hWnd, bool clickThrough)
        {
            IntPtr style = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            long current = style.ToInt64();
            if (clickThrough)
            {
                current |= (WS_EX_TRANSPARENT | WS_EX_LAYERED);
            }
            else
            {
                current &= ~WS_EX_TRANSPARENT;
            }
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(current));
        }

        public static void PinToBottom(IntPtr hWnd)
        {
            SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static bool IsAutostartEnabled()
        {
            return AutostartManager.IsEnabled();
        }

        public static bool SetAutostart(bool enable)
        {
            return AutostartManager.SetEnabled(enable);
        }

        public static bool IsPawnIoInstalled()
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                {
                    if (key == null) return false;
                    Version version;
                    return Version.TryParse(key.GetValue("DisplayVersion") as string, out version) &&
                        version >= new Version(2, 2);
                }
            }
            catch (Exception ex) { Diagnostics.Write("Verifica PawnIO fallita", ex); return false; }
        }

        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void RestartAsAdministrator()
        {
            var window = System.Windows.Application.Current.MainWindow as MainWindow;
            if (window != null) window.Restart(true);
        }
    }
}
