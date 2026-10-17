using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeskHardwareWidget
{
    public partial class App : Application
    {
        private Mutex _instanceMutex;
        private bool _ownsInstance;
        internal bool IsDiagnostic { get; set; }
        internal bool StartedAutomatically { get; private set; }
        internal bool AcquireInstance()
        {
            if (_instanceMutex == null)
                _instanceMutex = new Mutex(false, "Local\\DeskHardwareWidget-" + WindowsIdentity.GetCurrent().User.Value);
            try { _ownsInstance = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            return _ownsInstance;
        }
        internal void ReleaseInstance()
        {
            if (_ownsInstance) { _instanceMutex.ReleaseMutex(); _ownsInstance = false; }
        }
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // The regression harness creates its own windows and sampling session.
            if (IsDiagnostic) return;
            StartedAutomatically = Array.IndexOf(e.Args, "--autostart") >= 0;
            if (e.Args.Length == 1 && (e.Args[0] == "--enable-autostart" || e.Args[0] == "--disable-autostart"))
            {
                bool enable = e.Args[0] == "--enable-autostart";
                bool success = Win32Helper.SetAutostart(enable);
                if (success)
                {
                    var settings = ConfigManager.Load();
                    settings.AutostartWithWindows = enable;
                    ConfigManager.Save(settings);
                }
                Shutdown(success ? 0 : 1);
                return;
            }
            if (e.Args.Length > 0 && e.Args[0] == "--diagnostics")
            {
                IsDiagnostic = true;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string output = e.Args.Length > 1 ? Path.GetFullPath(e.Args[1]) : Path.Combine(AppPaths.DataDirectory, "diagnostics.json");
                int samples = 5;
                if (e.Args.Length > 2) int.TryParse(e.Args[2], out samples);
                samples = Math.Max(1, Math.Min(60, samples));
                try
                {
                    await Task.Run(delegate
                    {
                        var snapshots = new List<HardwareSnapshot>();
                        using (var monitor = new HardwareMonitor())
                            for (int i = 0; i < samples; i++)
                            {
                                snapshots.Add(monitor.Update());
                                if (i + 1 < samples) Thread.Sleep(1000);
                            }
                        using (var stream = File.Create(output))
                            new DataContractJsonSerializer(typeof(List<HardwareSnapshot>)).WriteObject(stream, snapshots);
                    });
                    Shutdown(0);
                }
                catch (Exception ex) { Diagnostics.Write("Diagnostica fallita", ex); Shutdown(1); }
                return;
            }
            if (!AcquireInstance())
            {
                if (!StartedAutomatically)
                    MessageBox.Show("OpenWidget è già in esecuzione. Usa l'icona nell'area notifiche.", "OpenWidget");
                Shutdown(); return;
            }
            DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs error)
            {
                Diagnostics.Write("Errore dell'interfaccia", error.Exception);
                MessageBox.Show("Errore del widget. I dettagli sono in widget.log.", "OpenWidget");
                error.Handled = true; Shutdown(1);
            };
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        protected override void OnExit(ExitEventArgs e)
        {
            ReleaseInstance();
            if (_instanceMutex != null) _instanceMutex.Dispose();
            base.OnExit(e);
        }
    }
}
