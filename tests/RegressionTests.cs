using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using DeskHardwareWidget;

class RegressionTests
{
    private static int _assertions;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _assertions++;
    }
    [STAThread]
    static int Main()
    {
        try
        {
            TestConfiguration();
            TestElevatedStartup();
            TestSensors();
            TestPlacementAndNetwork();
            TestBatteries();
            TestLogitechBatteries();
            TestDualSenseBatteries();
            TestUsbControllerVisibility();
            TestInterface();
            Console.WriteLine("PASS: " + _assertions + " controlli di regressione");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    static void TestElevatedStartup()
    {
        string executable = typeof(App).Assembly.Location;
        IntPtr module = LoadLibraryEx(executable, IntPtr.Zero, 2);
        Check(module != IntPtr.Zero, "Impossibile leggere il manifest compilato");
        try
        {
            IntPtr resource = FindResource(module, new IntPtr(1), new IntPtr(24));
            Check(resource != IntPtr.Zero, "Manifest amministratore assente dall'eseguibile");
            byte[] bytes = new byte[SizeofResource(module, resource)];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            var manifest = new XmlDocument { XmlResolver = null };
            manifest.LoadXml(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0'));
            var level = manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']");
            Check(level != null && level.Attributes["level"].Value == "requireAdministrator", "L'eseguibile non richiede sempre l'amministratore");
        }
        finally { FreeLibrary(module); }

        string path = @"C:\Program Files\Widget & sensori\DeskHardwareWidget.exe";
        string sid = "S-1-5-21-1-2-3-1001";
        string xml = AutostartManager.BuildTaskXml(path, sid);
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xml);
        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("t", AutostartManager.TaskNamespace);
        Func<string, string> value = query => document.SelectSingleNode("/t:Task/" + query, namespaces).InnerText;
        Check(value("t:Principals/t:Principal/t:RunLevel") == "HighestAvailable", "Avvio automatico senza privilegi elevati");
        Check(value("t:Principals/t:Principal/t:LogonType") == "InteractiveToken", "Il widget non usa il desktop interattivo");
        Check(value("t:Principals/t:Principal/t:UserId") == sid && value("t:Triggers/t:LogonTrigger/t:UserId") == sid, "Attività assegnata all'utente sbagliato");
        Check(value("t:Actions/t:Exec/t:Command") == path && value("t:Actions/t:Exec/t:WorkingDirectory") == Path.GetDirectoryName(path), "Percorso con spazi o & alterato");
        Check(value("t:Actions/t:Exec/t:Arguments") == "--autostart", "Avvio automatico non identificato");
        Check(value("t:Settings/t:ExecutionTimeLimit") == "PT0S", "Il widget viene terminato dopo 72 ore");
        Check(value("t:Settings/t:DisallowStartIfOnBatteries") == "false" && value("t:Settings/t:StopIfGoingOnBatteries") == "false", "Avvio o continuità a batteria impediti");
        Check(value("t:Settings/t:MultipleInstancesPolicy") == "IgnoreNew", "Istanze automatiche duplicate consentite");
        Check(AutostartManager.TaskMatches(xml, path, sid), "Attività appena generata non riconosciuta");
        Check(!AutostartManager.TaskMatches(xml, @"D:\Nuovo percorso\DeskHardwareWidget.exe", sid), "Percorso precedente non aggiornabile");
        Check(!AutostartManager.TaskMatches(xml, path, "S-1-5-21-1-2-3-1002"), "Attività di un altro utente accettata");
        Check(!AutostartManager.TaskMatches(xml.Replace("HighestAvailable", "LeastPrivilege"), path, sid), "Attività non elevata accettata");
        Check(!AutostartManager.TaskMatches(xml.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>"), path, sid), "Attività o trigger disabilitati accettati");
        Check(!AutostartManager.TaskMatches(xml.Replace("PT0S", "PT72H"), path, sid), "Limite di durata precedente accettato");
        Check(!AutostartManager.TaskMatches(xml.Replace("<Triggers>", "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger>"), path, sid), "Trigger aggiuntivo accettato");
        Check(!AutostartManager.TaskMatches(xml.Replace("--autostart", "--diagnostics"), path, sid), "Argomenti errati accettati");

        dynamic service = null, folder = null, definition = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.Connect();
            folder = service.GetFolder(@"\");
            string unusedName = "DeskHardwareWidget-Regression-" + Guid.NewGuid().ToString("N");
            Check(AutostartManager.FindTask(folder, unusedName) == null, "Attività inesistente non gestita dal binder COM");
            string accountName;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent()) { sid = identity.User.Value; accountName = identity.Name; }
            xml = AutostartManager.BuildTaskXml(path, sid);
            var persisted = new XmlDocument { XmlResolver = null };
            persisted.LoadXml(xml);
            foreach (string query in new[] { "t:Triggers/t:LogonTrigger/t:Enabled", "t:Settings/t:RunOnlyIfNetworkAvailable", "t:Settings/t:AllowStartOnDemand", "t:Settings/t:Enabled", "t:Settings/t:RunOnlyIfIdle", "t:Settings/t:Priority" })
            {
                var node = persisted.SelectSingleNode("/t:Task/" + query, namespaces);
                node.ParentNode.RemoveChild(node);
            }
            persisted.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger/t:UserId", namespaces).InnerText = accountName;
            Check(AutostartManager.TaskMatches(persisted.OuterXml, path, sid), "Default omessi e nome account risolto da Windows non riconosciuti");
            persisted.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger/t:UserId", namespaces).InnerText = "S-1-5-18";
            Check(!AutostartManager.TaskMatches(persisted.OuterXml, path, sid), "Trigger assegnato a SYSTEM accettato");
            definition = service.NewTask(0);
            definition.XmlText = xml;
            Check(AutostartManager.TaskMatches((string)definition.XmlText, path, sid), "XML normalizzato da Windows non riconosciuto");
            // TASK_VALIDATE_ONLY: use the real Windows schema without registering
            // a task or altering the user's startup configuration.
            folder.RegisterTask(unusedName, xml, 1, sid, null, 3, null);
            Check(AutostartManager.FindTask(folder, unusedName) == null, "La convalida ha registrato un'attività");
        }
        finally
        {
            if (definition != null) Marshal.FinalReleaseComObject((object)definition);
            if (folder != null) Marshal.FinalReleaseComObject((object)folder);
            if (service != null) Marshal.FinalReleaseComObject((object)service);
        }
    }
    static void TestConfiguration()
    {
        string path = AppPaths.ConfigPath;
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        File.WriteAllText(path, "{}");
        var empty = ConfigManager.Load();
        Check(empty.RefreshIntervalMs == 1000 && empty.ShowMemoryCard && empty.ShowNetworkCard, "Campi mancanti senza default");
        Check(!empty.AutostartWithWindows && empty.X == 80, "Default iniziali errati");
        Check(empty.HiddenGpuIds != null && empty.HiddenGpuIds.Count == 0, "Le configurazioni precedenti nascondono le GPU");
        Check(empty.BatteryWidgetEnabled && empty.BatteryShowPc && empty.BatteryShowAccessories && empty.BatteryRefreshSeconds == 30,
            "La configurazione precedente non riceve i default delle batterie");
        var batterySettings = new WidgetSettings { BatteryX = double.NaN, BatteryY = double.PositiveInfinity, BatteryRefreshSeconds = 1, BatteryClickThrough = true };
        batterySettings.Validate();
        Check(batterySettings.BatteryX == 80 && batterySettings.BatteryY == 540 && batterySettings.BatteryRefreshSeconds == 10 && batterySettings.BatteryIsLocked,
            "Impostazioni batterie non validate");
        File.WriteAllText(path, "{\"HiddenGpuIds\":null}");
        Check(ConfigManager.Load().HiddenGpuIds.Count == 0, "Lista GPU nulla non gestita");
        var gpuSettings = new WidgetSettings { HiddenGpuIds = new List<string> { "/gpu/1", "/gpu/1", "", null } };
        ConfigManager.Save(gpuSettings);
        Check(ConfigManager.Load().HiddenGpuIds.SequenceEqual(new[] { "/gpu/1" }), "Selezione GPU non normalizzata o non persistente");
        File.WriteAllText(path, "{\"IsLocked\":true,\"AutostartWithWindows\":true,\"X\":-1800,\"RefreshIntervalMs\":-1}");
        var partial = ConfigManager.Load();
        Check(partial.IsLocked && partial.AutostartWithWindows && partial.X == -1800, "Impostazioni esistenti perse");
        Check(partial.RefreshIntervalMs == 1000, "Intervallo negativo accettato");
        File.WriteAllText(path, "{\"IsLocked\":true,\"AutostartWithWindows\":true,\"X\":3280,\"Y\":0,\"RefreshIntervalMs\":1000,\"ShowMemoryCard\":true,\"ShowNetworkCard\":true}", Encoding.UTF8);
        var legacy = ConfigManager.Load();
        Check(legacy.IsLocked && legacy.AutostartWithWindows && legacy.X == 3280 && legacy.Y == 0, "Configurazione precedente con BOM UTF-8 non preservata");
        partial.RefreshIntervalMs = 0; partial.Validate();
        Check(partial.RefreshIntervalMs == 1000, "Timer a zero");
        partial.RefreshIntervalMs = 1; partial.Validate();
        Check(partial.RefreshIntervalMs == 250, "Intervallo troppo frequente");
        partial.RefreshIntervalMs = int.MaxValue; partial.X = double.NaN; partial.Y = double.PositiveInfinity; partial.Validate();
        Check(partial.RefreshIntervalMs == 60000 && partial.X == 80 && partial.Y == 80, "Valori estremi non normalizzati");
        ConfigManager.Save(new WidgetSettings { X = -500, IsLocked = true });
        ConfigManager.Save(new WidgetSettings { X = 400, IsLocked = true });
        Check(ConfigManager.Load().X == 400 && File.Exists(path + ".bak"), "Salvataggio atomico senza backup");
        File.WriteAllText(path, "{broken");
        Check(ConfigManager.Load().X == -500, "Configurazione corrotta: backup non recuperato");
        ConfigManager.Save(new WidgetSettings { IsLocked = true });
    }
    static void TestSensors()
    {
        var sensors = new List<SensorReading>
        {
            new SensorReading(SensorType.Temperature, "P-Core Distance to TjMax", 95),
            new SensorReading(SensorType.Temperature, "Core Max", 72),
            new SensorReading(SensorType.Temperature, "CPU Package", 64.5),
            new SensorReading(SensorType.Temperature, "Other", double.NaN)
        };
        Check(HardwareMonitor.SelectTemperature(sensors, true) == 64.5, "Priorità temperatura CPU errata");
        sensors[2].Value = null;
        Check(HardwareMonitor.SelectTemperature(sensors, true) == 72, "Fallback CPU non disponibile");
        sensors[1].Value = null;
        Check(!HardwareMonitor.SelectTemperature(sensors, true).HasValue, "Distanza da TjMax usata come temperatura");
        var missing = HardwareMonitor.CreateGpuSnapshot("missing", "GPU", "GpuNvidia", new List<SensorReading>());
        Check(!missing.HasTemperature && !missing.HasLoad && !missing.HasMemory, "Sensori mancanti presentati come zero");
        Check(!missing.TemperatureText.Contains("0 °C"), "Temperatura GPU inventata");
        var first = MakeNvidia(48.125);
        var second = MakeIntel();
        Check(first.HasTemperature && first.TemperatureC == 48.125 && first.LoadPercent == 42, "Telemetria NVIDIA errata");
        Check(first.HasMemory && !first.IsSharedMemory && first.MemoryUsedMb == 256 && first.MemoryTotalMb == 8192, "VRAM NVIDIA errata");
        Check(!second.HasTemperature && second.HasLoad && second.LoadPercent == 31, "Carico D3D Intel errato");
        Check(second.IsSharedMemory && second.MemoryUsedMb == 950, "Memoria condivisa Intel errata");
        Check(first.Id != second.Id, "GPU fuse in una sola identità");
        Check(MakeNvidia(48.4).TemperatureC != first.TemperatureC, "Temperatura memorizzata invece di aggiornata");
    }
    static GpuSnapshot MakeNvidia(double temperature)
    {
        return HardwareMonitor.CreateGpuSnapshot("/gpu-nvidia/0", "NVIDIA GeForce RTX 5060 Laptop GPU", "GpuNvidia", new List<SensorReading>
        {
            new SensorReading(SensorType.Temperature, "GPU Core", temperature),
            new SensorReading(SensorType.Temperature, "GPU Hot Spot", 80),
            new SensorReading(SensorType.Load, "GPU Core", 42),
            new SensorReading(SensorType.SmallData, "GPU Memory Used", 256),
            new SensorReading(SensorType.SmallData, "GPU Memory Total", 8192)
        });
    }
    static GpuSnapshot MakeIntel()
    {
        return HardwareMonitor.CreateGpuSnapshot("/gpu-intel/0", "Intel(R) UHD Graphics", "GpuIntel", new List<SensorReading>
        {
            new SensorReading(SensorType.Load, "D3D 3D", 10),
            new SensorReading(SensorType.Load, "D3D Video Decode", 31),
            new SensorReading(SensorType.SmallData, "D3D Shared Memory Used", 950),
            new SensorReading(SensorType.SmallData, "D3D Shared Memory Total", 7994)
        });
    }
    static void TestPlacementAndNetwork()
    {
        var areas = new List<Rect> { new Rect(-1920, 0, 1920, 1040), new Rect(0, 0, 1920, 1040) };
        var negative = WindowPlacement.Clamp(new Rect(-1700, 80, 566, 420), areas);
        Check(negative.X == -1700 && negative.Y == 80, "Posizione su monitor a sinistra scartata");
        var right = WindowPlacement.Clamp(new Rect(1890, 1020, 566, 420), areas);
        Check(areas.Any(area => area.Contains(new Rect(right.X, right.Y, 566, 420))), "Widget parzialmente fuori schermo");
        var disconnected = WindowPlacement.Clamp(new Rect(-3000, -1000, 566, 420), new List<Rect> { areas[1] });
        Check(areas[1].Contains(new Rect(disconnected.X, disconnected.Y, 566, 420)), "Monitor rimosso non gestito");
        Check(HardwareMonitor.CounterDelta(1000, 100) == 0 && HardwareMonitor.CounterDelta(100, 1000) == 900, "Reset dei contatori rete errato");
        Check(HardwareMonitor.FormatSpeed(1000).EndsWith("Mbps") && HardwareMonitor.FormatSpeed(999).EndsWith("Kbps"), "Unità decimali di rete errate");
    }
    static void TestInterface()
    {
        var application = new App { IsDiagnostic = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TestApplicationIcon();
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var snapshot = new HardwareSnapshot
        {
            CpuName = "Intel Core i7-14650HX", HasCpuLoad = true, CpuLoadPercent = 24,
            HasCpuTemp = true, CpuTempC = 65.2, CpuSubtitle = "65.2 °C",
            HasRam = true, RamLoadPercent = 62, RamSubtitle = "9.7 / 15.6 GB",
            DownloadSpeedKbps = 54000, DownloadFormatted = "54.0 Mbps", UploadFormatted = "1.2 Mbps", NetworkInterfaceName = "Wi-Fi",
            Gpus = new List<GpuSnapshot> { MakeNvidia(48.125), MakeIntel() }
        };
        window.RenderSnapshot(snapshot);
        Layout(root);
        window.RenderSnapshot(snapshot);
        Layout(root);
        var panel = (WrapPanel)window.FindName("CardsPanel");
        var cards = panel.Children.OfType<GpuCard>().ToList();
        Check(cards.Count == 2, "Seconda scheda GPU assente");
        string before = ((TextBlock)cards[0].FindName("txtTemperature")).Text;
        snapshot.Gpus[0] = MakeNvidia(48.4);
        window.RenderSnapshot(snapshot);
        Check(((TextBlock)cards[0].FindName("txtTemperature")).Text != before, "Temperatura UI ferma");
        Check(((TextBlock)cards[1].FindName("txtTemperature")).Text.Contains("non disponibile"), "Temperatura Intel inventata");
        var line = (System.Windows.Shapes.Path)window.FindName("cpuGraphLine");
        var fill = (System.Windows.Shapes.Path)window.FindName("cpuGraphFill");
        GraphRenderer.Draw(line, fill, Enumerable.Range(0, 28).Select(i => (double)i).ToList(), 100);
        var canvas = (Canvas)line.Parent;
        Check(line.Data.Bounds.Right <= canvas.ActualWidth - 0.5, "Ultimo campione del grafico tagliato");
        Check(Math.Abs(line.Data.Bounds.Right - (canvas.ActualWidth - 1)) < 0.01, "Grafico non adattato all'area reale");
        snapshot.Gpus.Add(HardwareMonitor.CreateGpuSnapshot("/gpu-amd/0", "AMD GPU", "GpuAmd", new List<SensorReading>()));
        window.RenderSnapshot(snapshot);
        Check(panel.Children.OfType<GpuCard>().Count() == 3 && panel.Children.OfType<GpuCard>().First() == cards[0], "Identità o cronologia perse aggiungendo una GPU");
        snapshot.Gpus.RemoveAt(2); window.RenderSnapshot(snapshot); Layout(root);
        var compact = window.GetCommands().First(command => command != null && command.Label.StartsWith("Modalità compatta"));
        compact.Execute();
        Check(((Border)window.FindName("cardMemory")).Visibility == Visibility.Collapsed && ((Border)window.FindName("cardNetwork")).Visibility == Visibility.Collapsed, "Modalità compatta incompleta");
        Check(panel.Children.OfType<GpuCard>().Count() == 2, "Modalità compatta nasconde la seconda GPU");
        compact.Execute(); Layout(root); window.RenderSnapshot(snapshot);
        TestGpuVisibility(window, snapshot, root);
        Check(window.GetCommands().Any(command => command != null && command.Label.Contains("PawnIO")), "Installazione sensori assente dai menu");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.DesiredSize.Width), (int)Math.Ceiling(root.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "widget-preview.png"))) encoder.Save(stream);
        TestLiveWindow(application);
    }
    static void TestBatteries()
    {
        var charging = BatteryMonitor.CreatePc(new BatteryMonitor.PowerStatus { AcLineStatus = 1, BatteryFlag = 9, BatteryLifePercent = 81, BatteryLifeTime = uint.MaxValue });
        Check(charging.Percent == 81 && charging.IsCharging == true && charging.IsOnAc == true && charging.RemainingSeconds == null, "Carica PC o stato alimentatore errato");
        var plugged = BatteryMonitor.CreatePc(new BatteryMonitor.PowerStatus { AcLineStatus = 1, BatteryFlag = 1, BatteryLifePercent = 80 });
        Check(plugged.IsCharging == false && plugged.StatusText == "Alimentatore collegato", "Alimentatore collegato confuso con ricarica attiva");
        Check(BatteryMonitor.CreatePc(new BatteryMonitor.PowerStatus { BatteryFlag = 128 }) == null, "Batteria PC inventata quando assente");
        var unknown = BatteryMonitor.CreatePc(new BatteryMonitor.PowerStatus { AcLineStatus = 255, BatteryFlag = 255, BatteryLifePercent = 255, BatteryLifeTime = uint.MaxValue });
        Check(unknown.Percent == null && unknown.IsCharging == null && unknown.IsOnAc == null, "Flag sconosciuti interpretati come batteria assente o in carica");
        Check(BatteryMonitor.ValidPercent((byte)0) == 0 && BatteryMonitor.ValidPercent((uint)100) == 100 && BatteryMonitor.ValidPercent((byte)255) == null && BatteryMonitor.ValidPercent(-1) == null,
            "Percentuali batterie invalide o zero gestiti male");
        var nodes = new[] {
            new BatteryMonitor.DeviceNode { Id = "root", Container = "mouse", Name = "MX Master 3S", IsRoot = true, IsBluetooth = true, Connected = true },
            new BatteryMonitor.DeviceNode { Id = "service", Container = "mouse", Name = "Battery service", IsBluetooth = true, Percent = 90 },
            new BatteryMonitor.DeviceNode { Id = "offline", Container = "headset", Name = "Headphones", IsRoot = true, Connected = false, Percent = 75, Charging = true }
        };
        var accessories = BatteryMonitor.BuildAccessories(nodes);
        var mouse = accessories.Single(device => device.Id == "mouse");
        Check(accessories.Count == 2 && mouse.Name == "MX Master 3S" && mouse.Percent == 90 && mouse.Kind == "Mouse", "Servizi duplicati o nome accessorio non raggruppati per dispositivo");
        Check(mouse.IsCharging == null, "Ricarica non comunicata trasformata in falso stato di ricarica");
        var offline = accessories.Single(device => device.Id == "headset");
        Check(offline.Percent == null && offline.IsCharging == null && offline.StatusText == "Scollegato", "Un accessorio scollegato mostra una percentuale precedente come attuale");
        var snapshot = new BatterySnapshot { Devices = new List<BatteryDeviceSnapshot> { charging, mouse, offline, unknown } };
        var settings = new WidgetSettings();
        Check(BatteryWindow.VisibleDevices(snapshot, settings).Count == 3, "Filtro scollegati non applicato");
        settings.BatteryShowUnknown = false;
        Check(BatteryWindow.VisibleDevices(snapshot, settings).Count == 2, "Filtro cariche sconosciute non applicato");
        settings.BatteryShowPc = false;
        Check(BatteryWindow.VisibleDevices(snapshot, settings).Single() == mouse, "Filtro batteria PC non indipendente");
        settings.BatteryShowAccessories = false;
        Check(BatteryWindow.VisibleDevices(snapshot, settings).Count == 0, "Filtro accessori non applicato");
        Check(BatteryCard.CreateArc(0).IsEmpty() && BatteryCard.CreateArc(100).IsEmpty() && !BatteryCard.CreateArc(50).IsEmpty(), "Anello batteria 0/50/100 errato");
        var card = new BatteryCard();
        card.UpdateBattery(unknown);
        Check(((TextBlock)card.FindName("PercentText")).Text == "—" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed, "L'interfaccia inventa la carica o il fulmine");
        card.UpdateBattery(plugged);
        Check(((Border)card.FindName("AcBadge")).Visibility == Visibility.Visible && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed,
            "Il PC collegato ma non in ricarica non ha un indicatore distinto");
        card.UpdateBattery(charging);
        Check(((TextBlock)card.FindName("PercentText")).Text == "81 %" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Visible && ((Border)card.FindName("AcBadge")).Visibility == Visibility.Collapsed,
            "La ricarica effettiva non è visibile o è confusa con il solo alimentatore");
        charging.Percent = 100; card.UpdateBattery(charging);
        Check(((System.Windows.Shapes.Ellipse)card.FindName("FullRing")).Visibility == Visibility.Visible, "Anello al 100% non completo");
        charging.Percent = 0; card.UpdateBattery(charging);
        Check(((TextBlock)card.FindName("PercentText")).Text == "0 %" && ((System.Windows.Shapes.Ellipse)card.FindName("FullRing")).Visibility == Visibility.Collapsed, "Batteria a zero confusa con dato sconosciuto");
        var native = new BatteryMonitor().Update(true);
        Check(native.Devices.Select(device => device.Id).Distinct().Count() == native.Devices.Count, "La rilevazione reale duplica i dispositivi");
        Check(native.Devices.All(device => !device.Percent.HasValue || device.Percent >= 0 && device.Percent <= 100), "La rilevazione reale restituisce percentuali fuori scala");
        using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "battery-diagnostics.json")))
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(BatterySnapshot)).WriteObject(stream, native);
    }
    static byte[] DualSenseUsbReport(byte battery, byte power)
    {
        var report = new byte[64]; report[0] = 0x01; report[53] = battery; report[54] = power;
        return report;
    }
    static void TestDualSenseBatteries()
    {
        Check(DualSenseBatteryProvider.IsSupported(0x054C, 0x0CE6) && DualSenseBatteryProvider.IsSupported(0x054C, 0x0DF2) &&
            !DualSenseBatteryProvider.IsSupported(0x054C, 0x05C4) && !DualSenseBatteryProvider.IsSupported(0x046D, 0x0CE6),
            "Controller diversi da DualSense/Edge inclusi nella lettura Sony");
        var device = new BatteryDeviceSnapshot { Id = "dualsense-test", WindowsContainerId = "bt-container", Name = "DualSense", Kind = "Gamepad", IsDualSenseHid = true, Connection = "USB" };
        var usb = DualSenseUsbReport(0x09, 0);
        Check(DualSenseBatteryProvider.TryApplyReport(device, usb, usb.Length, false) && device.Percent == 95 &&
            device.IsCharging == false && device.IsOnAc == false && device.IsConnected == true && device.StatusText == "A batteria", "Batteria DualSense USB decodificata male");
        var card = new BatteryCard(); card.UpdateBattery(device);
        Check(card.ToolTip.ToString().Contains("USB · lettura diretta DualSense HID") && card.ToolTip.ToString().Contains("10%") &&
            !card.ToolTip.ToString().Contains("fornito da Windows"), "Origine o granularità DualSense non indicata");
        foreach (var level in new[] { new { Raw = (byte)0x00, Percent = 5 }, new { Raw = (byte)0x0A, Percent = 100 },
            new { Raw = (byte)0x10, Percent = 0 }, new { Raw = (byte)0x18, Percent = 70 }, new { Raw = (byte)0x1A, Percent = 90 } })
        {
            usb = DualSenseUsbReport(level.Raw, 0x10);
            Check(DualSenseBatteryProvider.TryApplyReport(device, usb, 64, false) && device.Percent == level.Percent,
                "La percentuale non segue la codifica di DualSenseClient");
        }
        card.UpdateBattery(device);
        Check(device.IsCharging == true && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Visible,
            "Ricarica DualSense non indicata dal fulmine");
        usb = DualSenseUsbReport(0x2A, 0x18); DualSenseBatteryProvider.TryApplyReport(device, usb, 64, false); card.UpdateBattery(device);
        Check(device.Percent == 100 && device.IsCharging == false && device.StatusText == "Carica completa" && device.IsOnAc == true &&
            ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed && ((System.Windows.Shapes.Ellipse)card.FindName("FullRing")).Visibility == Visibility.Visible,
            "Carica completa DualSense confusa con ricarica attiva");
        usb = DualSenseUsbReport(0x08, 0x10); DualSenseBatteryProvider.TryApplyReport(device, usb, 64, false);
        Check(device.IsCharging == false && device.StatusText == "Collegato · non in carica", "Alimentazione USB DualSense confusa con ricarica");
        foreach (byte state in new byte[] { 0xA8, 0xB8, 0xF8 })
        {
            usb = DualSenseUsbReport(state, 0x10); DualSenseBatteryProvider.TryApplyReport(device, usb, 64, false); card.UpdateBattery(device);
            Check(device.Percent == null && device.IsCharging == false && device.BatteryStatusText != null &&
                ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed, "Errore DualSense mostra ricarica o percentuale inventata");
        }
        foreach (byte invalid in new byte[] { 0x0B, 0x1F, 0x2F, 0x38, 0xFF })
        {
            usb = DualSenseUsbReport(invalid, 0); DualSenseBatteryProvider.TryApplyReport(device, usb, 64, false);
            Check(device.Percent == null, "Livello/stato batteria DualSense sconosciuto accettato come percentuale");
        }
        // Golden Bluetooth fixture; CRC32 independently calculated with Python zlib:
        // zlib.crc32(bytes([0xA1]) + report[:74]) == 0x72C31700.
        var bluetooth = new byte[78]; bluetooth[0] = 0x31; bluetooth[54] = 0x18; bluetooth[55] = 0x10;
        bluetooth[74] = 0x00; bluetooth[75] = 0x17; bluetooth[76] = 0xC3; bluetooth[77] = 0x72;
        device.IsBluetooth = true; device.Connection = "Bluetooth";
        Check(DualSenseBatteryProvider.TryApplyReport(device, bluetooth, 78, true) && device.Percent == 70 && device.IsCharging == true && device.IsOnAc == true,
            "Offset batteria o CRC Bluetooth DualSense errati");
        card.UpdateBattery(device);
        Check(card.ToolTip.ToString().Contains("Bluetooth · lettura diretta DualSense HID") && !card.ToolTip.ToString().Contains("Windows"), "Dato Bluetooth Sony attribuito a Windows");
        bluetooth[54] ^= 1;
        Check(!DualSenseBatteryProvider.TryApplyReport(device, bluetooth, 78, true) && device.Percent == null && device.ReportedAtUtc == null,
            "Report Bluetooth corrotto accettato o carica precedente mantenuta");
        var simple = new byte[78]; simple[0] = 1; simple[53] = 0x2A;
        Check(!DualSenseBatteryProvider.TryApplyReport(device, simple, 78, true) && device.Percent == null, "Report Bluetooth semplice padded interpretato come USB");
        Check(!DualSenseBatteryProvider.TryApplyReport(device, null, 64, false) &&
            !DualSenseBatteryProvider.TryApplyReport(device, usb, 63, false) && !DualSenseBatteryProvider.TryApplyReport(device, usb, 65, false) &&
            !DualSenseBatteryProvider.TryApplyReport(device, bluetooth, 65, true) && !DualSenseBatteryProvider.TryApplyReport(device, bluetooth, 78, false), "Report DualSense troncato o di altro trasporto accettato");
        var pairing = new byte[20]; pairing[0] = 9;
        for (int i = 1; i <= 6; i++) pairing[i] = (byte)i;
        Check(DualSenseBatteryProvider.ReadControllerAddress(pairing) == "060504030201" &&
            DualSenseBatteryProvider.ReadControllerAddress(new byte[20]) == null && DualSenseBatteryProvider.ReadControllerAddress(new byte[] { 9, 1 }) == null,
            "Identità controller non valida o diversa tra USB e Bluetooth");
        usb = DualSenseUsbReport(0x09, 0); DualSenseBatteryProvider.TryApplyReport(device, usb, 64, false);
        var provider = new DualSenseBatteryProvider(); provider.CompleteScan(new[] { device });
        var offline = provider.CompleteScan(new BatteryDeviceSnapshot[0]).Single();
        Check(offline.IsConnected == false && offline.Percent == null && offline.IsCharging == null && offline.IsOnAc == null && offline.ReportedAtUtc == null,
            "Un controller rimosso conserva una lettura precedente come attuale");
        var second = new BatteryDeviceSnapshot { Id = "second-controller", Name = "DualSense", WindowsContainerId = "other-container", IsConnected = true, Percent = 25 };
        var windows = new BatteryDeviceSnapshot { Id = "BT-CONTAINER", Name = "Wireless Controller", IsConnected = true, Percent = 10 };
        var merged = DualSenseBatteryProvider.Merge(new[] { windows, second }, new[] { device });
        Check(merged.Count == 2 && merged.Contains(device) && merged.Contains(second) && !merged.Contains(windows), "DualSense duplicato o due controller omonimi uniti");
        var inaccessible = new BatteryDeviceSnapshot { Id = device.Id, WindowsContainerId = "bt-container", Name = "DualSense", IsDualSenseHid = true };
        merged = DualSenseBatteryProvider.Merge(new[] { windows }, new[] { inaccessible });
        Check(merged.Count == 1 && merged[0] == windows, "Controller HID occupato nasconde una percentuale Windows valida");
        var wired = new BatteryDeviceSnapshot { Id = device.Id, WindowsContainerId = "usb-container", Name = "DualSense", IsConnected = true, IsDualSenseHid = true, Connection = "USB" };
        usb = DualSenseUsbReport(0x18, 0x10); DualSenseBatteryProvider.TryApplyReport(wired, usb, 64, false);
        var switched = provider.CompleteScan(new[] { wired });
        Check(switched.Count == 1, "Passare da Bluetooth a USB crea un controller scollegato duplicato");
        merged = DualSenseBatteryProvider.Merge(new[] { windows, new BatteryDeviceSnapshot { Id = "USB-CONTAINER", Percent = 40 } }, switched);
        Check(merged.Count == 1 && merged[0] == wired, "Cambio trasporto non unisce i nodi Windows del medesimo controller");
        merged = DualSenseBatteryProvider.Merge(new BatteryDeviceSnapshot[0], new[] { device, wired });
        Check(merged.Count == 1 && merged[0] == wired, "Due interfacce dello stesso controller creano due anelli");
        using (var cancelled = new System.Threading.CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { provider.Read(cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "La lettura DualSense ignora la richiesta di arresto");
        }
    }
    static void TestUsbControllerVisibility()
    {
        Check(DualSenseBatteryProvider.UsbControllerName(@"USB\VID_054C&PID_0CE6\controller-1") == "DualSense" &&
            DualSenseBatteryProvider.UsbControllerName(@"hid\vid_054c&pid_0df2&mi_03\controller-2") == "DualSense Edge", "Nodi USB/HID Sony non riconosciuti");
        Check(DualSenseBatteryProvider.UsbControllerName(@"USB\VID_054C&PID_0CE60\other") == null &&
            DualSenseBatteryProvider.UsbControllerName(@"USB\VID_046D&PID_0CE6\other") == null &&
            DualSenseBatteryProvider.UsbControllerName(@"BTHENUM\VID_054C&PID_0CE6\paired") == null &&
            DualSenseBatteryProvider.UsbControllerName(null) == null, "Dispositivo diverso o Bluetooth scambiato per controller USB");
        var nodes = new[] {
            new BatteryMonitor.DeviceNode { Id = "usb-1", Container = "wired-1", Name = "DualSense", IsRoot = true, IsUsbController = true },
            new BatteryMonitor.DeviceNode { Id = "usb-audio", Container = "wired-1", Name = "DualSense", IsUsbController = true },
            new BatteryMonitor.DeviceNode { Id = "old-bt", Container = "wired-1", Name = "Wireless Controller", IsRoot = true, IsBluetooth = true, Percent = 45, Connected = false, Charging = true },
            new BatteryMonitor.DeviceNode { Id = "usb-2", Container = "wired-2", Name = "DualSense", IsRoot = true, IsUsbController = true }
        };
        var devices = BatteryMonitor.BuildAccessories(nodes);
        Check(devices.Count == 2 && devices.All(item => item.Kind == "Gamepad" && item.Connection == "USB" && item.IsConnected == true &&
            item.IsReadingUnavailable && item.Percent == null && item.IsCharging == null && !item.IsBluetooth),
            "Controller USB senza batteria nascosto, duplicato oppure usa dati Bluetooth precedenti");
        var snapshot = new BatterySnapshot { Devices = devices };
        var settings = new WidgetSettings { BatteryShowUnknown = false, BatteryShowDisconnected = false };
        Check(BatteryWindow.VisibleDevices(snapshot, settings).Count == 2, "Filtro valori sconosciuti nasconde i controller USB presenti");
        var card = new BatteryCard(); card.UpdateBattery(devices[0]);
        Check(((TextBlock)card.FindName("PercentText")).Text == "—" && ((TextBlock)card.FindName("PowerStateText")).Text == "USB · batteria N/D" &&
            ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed && card.ToolTip.ToString().Contains("USB · dispositivo rilevato da Windows"),
            "La presenza USB viene confusa con una percentuale o una ricarica confermata");
        var prior = new BatteryDeviceSnapshot { Id = "dualsense-existing", Name = "DualSense", WindowsContainerId = "wired-1", IsDualSenseHid = true };
        var offline = DualSenseBatteryProvider.Unavailable(prior);
        var merged = DualSenseBatteryProvider.Merge(devices, new[] { offline });
        Check(merged.Count == 2 && merged.Single(item => item.Id == prior.Id).IsConnected == true &&
            BatteryWindow.VisibleDevices(new BatterySnapshot { Devices = merged }, settings).Count == 2,
            "Una vecchia lettura HID scollegata nasconde il controller USB presente in Windows");
        var wired = merged.Single(item => item.Id == prior.Id);
        var report = DualSenseUsbReport(0x18, 0x10);
        DualSenseBatteryProvider.TryApplyReport(wired, report, 64, false); card.UpdateBattery(wired);
        Check(wired.Percent == 70 && wired.IsCharging == true && !wired.IsReadingUnavailable &&
            ((TextBlock)card.FindName("PowerStateText")).Text == "USB · In carica" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Visible,
            "La ripresa della telemetria USB non aggiorna carica e ricarica");
        settings.BatteryShowAccessories = false;
        Check(BatteryWindow.VisibleDevices(snapshot, settings).Count == 0, "I controller USB ignorano il filtro degli accessori");
    }
    static void TestLogitechBatteries()
    {
        var mouse = new BatteryDeviceSnapshot { Id = "logitech-test", Name = "MX Master 3S", Kind = "Mouse", IsConnected = true, IsLogitechHid = true, Connection = "Logi Bolt / USB" };
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 90, 8, 0 }, true);
        Check(mouse.Percent == 90 && mouse.IsCharging == false && mouse.StatusText == "A batteria" && mouse.ReportedAtUtc.HasValue, "Lettura Logitech HID++ non decodificata");
        var card = new BatteryCard(); card.UpdateBattery(mouse);
        Check(((TextBlock)card.FindName("PercentText")).Text == "90 %" && card.ToolTip.ToString().Contains("lettura diretta Logitech HID++") && !card.ToolTip.ToString().Contains("fornito da Windows"), "Origine o percentuale Logitech errata nell'interfaccia");
        foreach (byte state in new byte[] { 1, 2 })
        {
            LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 90, 8, state }, true);
            card.UpdateBattery(mouse);
            Check(mouse.IsCharging == true && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Visible, "Ricarica Logitech attiva senza fulmine");
        }
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 100, 8, 3 }, true);
        card.UpdateBattery(mouse);
        Check(mouse.IsCharging == false && mouse.StatusText == "Carica completa" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed && ((TextBlock)card.FindName("PowerStateText")).Text == "Carica completa", "Carica Logitech completa confusa con ricarica attiva");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 75, 4, 4 }, true);
        card.UpdateBattery(mouse);
        Check(mouse.IsCharging == false && mouse.StatusText == "Errore di ricarica" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed, "Errore Logitech confuso con ricarica");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 0, 1, 0 }, false);
        Check(mouse.Percent == null, "Dispositivo Logitech senza percentuale mostra uno zero inventato");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 0, 1, 0 }, true);
        Check(mouse.Percent == 0, "Batteria Logitech unificata a zero confusa con valore sconosciuto");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1000, new byte[] { 0, 0, 1 }, true);
        Check(mouse.Percent == null && mouse.IsCharging == true, "Zero legacy HID++ deve indicare percentuale sconosciuta");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1000, new byte[] { 50, 20, 4 }, true);
        Check(mouse.Percent == 50 && mouse.IsCharging == true && mouse.StatusText == "Ricarica lenta", "Stato 4 legacy confuso con errore unificato");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1000, new byte[] { 100, 0, 3 }, true);
        Check(mouse.IsCharging == false && mouse.StatusText == "Carica completa", "Carica legacy completa interpretata come ricarica");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 255, 0, 255 }, true);
        Check(mouse.Percent == null && mouse.IsCharging == null && mouse.BatteryStatusText == null, "Valori Logitech non validi interpretati come stato noto");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, null, true);
        Check(mouse.Percent == null && mouse.IsCharging == null && mouse.ReportedAtUtc == null, "Timeout HID conserva una lettura precedente");
        Check(mouse.IsReadingUnavailable && mouse.StatusText == "Lettura non disponibile", "Richiesta batteria fallita non segnala la lettura indisponibile");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 90, 8 }, true);
        Check(mouse.Percent == null && mouse.IsCharging == null, "Risposta HID troncata interpretata come valida");
        LogitechBatteryProvider.ApplyBattery(mouse, 0x1004, new byte[] { 90, 8, 1 }, true);
        var offline = LogitechBatteryProvider.Unavailable(mouse);
        Check(offline.Id == mouse.Id && offline.Name == mouse.Name && offline.IsConnected == null && offline.IsReadingUnavailable && offline.Percent == null && offline.IsCharging == null && offline.ReportedAtUtc == null, "Timeout Logitech confuso con disconnessione o mantiene dati precedenti");
        var timeoutSettings = new WidgetSettings { BatteryShowUnknown = false, BatteryShowDisconnected = false };
        var timeoutSnapshot = new BatterySnapshot { Devices = new List<BatteryDeviceSnapshot> { offline } };
        Check(BatteryWindow.VisibleDevices(timeoutSnapshot, timeoutSettings).Single() == offline, "Timeout Logitech fa sparire il mouse con i filtri predefiniti o cariche sconosciute nascoste");
        card.UpdateBattery(offline);
        Check(((TextBlock)card.FindName("PowerStateText")).Text == "Lettura non disponibile" && ((TextBlock)card.FindName("PercentText")).Text == "—" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed, "Timeout mostra percentuale o ricarica precedenti");
        var repeatedTimeout = LogitechBatteryProvider.Unavailable(offline);
        Check(repeatedTimeout.Id == mouse.Id && repeatedTimeout.IsConnected == null && repeatedTimeout.IsReadingUnavailable, "Timeout consecutivi perdono l'identità del mouse");
        var connectionTracker = new LogitechBatteryProvider();
        var firstMiss = connectionTracker.MissedPing(mouse);
        var secondMiss = connectionTracker.MissedPing(firstMiss);
        Check(firstMiss.IsConnected == null && secondMiss.IsConnected == null && firstMiss.IsReadingUnavailable && secondMiss.IsReadingUnavailable,
            "Un timeout isolato o due letture fallite vengono scambiati subito per disconnessione");
        var disconnected = connectionTracker.MissedPing(secondMiss);
        Check(disconnected.IsConnected == false && !disconnected.IsReadingUnavailable && disconnected.StatusText == "Scollegato" &&
            disconnected.Percent == null && disconnected.IsCharging == null && disconnected.ReportedAtUtc == null,
            "Mouse assente resta indefinitamente in lettura non disponibile o conserva vecchi dati");
        var disconnectedSnapshot = new BatterySnapshot { Devices = new List<BatteryDeviceSnapshot> { disconnected } };
        Check(BatteryWindow.VisibleDevices(disconnectedSnapshot, timeoutSettings).Count == 0, "Mouse spento non rispetta il filtro dei dispositivi scollegati");
        timeoutSettings.BatteryShowDisconnected = true;
        Check(BatteryWindow.VisibleDevices(disconnectedSnapshot, timeoutSettings).Single() == disconnected, "Dispositivo scollegato non visibile quando richiesto");
        card.UpdateBattery(disconnected);
        Check(((TextBlock)card.FindName("PowerStateText")).Text == "Scollegato" && ((Border)card.FindName("ChargingBadge")).Visibility == Visibility.Collapsed,
            "Mouse assente mostra ancora lettura non disponibile o ricarica");
        var staleWindows = new BatteryDeviceSnapshot { Id = mouse.Id, Percent = 90, IsCharging = true, IsConnected = true };
        Check(LogitechBatteryProvider.Merge(new[] { staleWindows }, new[] { disconnected }).Single() == disconnected,
            "Dati Windows precedenti fanno ricomparire un mouse non raggiungibile");
        var otherMouse = new BatteryDeviceSnapshot { Id = "second-logitech", Name = "MX Master 3S", IsLogitechHid = true };
        Check(connectionTracker.MissedPing(otherMouse).IsConnected == null, "Le letture fallite di un mouse influenzano un altro dispositivo");
        connectionTracker.ConfirmConnection(mouse.Id);
        var recovered = new BatteryDeviceSnapshot { Id = mouse.Id, Name = mouse.Name, IsLogitechHid = true, IsConnected = true };
        LogitechBatteryProvider.ApplyBattery(recovered, 0x1004, new byte[] { 88, 8, 0 }, true);
        Check(BatteryWindow.VisibleDevices(new BatterySnapshot { Devices = new List<BatteryDeviceSnapshot> { recovered } }, new WidgetSettings()).Single().Percent == 88,
            "Mouse tornato su questo PC non riappare con i dati aggiornati");
        Check(connectionTracker.MissedPing(recovered).IsConnected == null, "Una risposta riuscita non azzera il conteggio delle assenze");
        connectionTracker.ConfirmConnection(mouse.Id);
        LogitechBatteryProvider.ApplyBattery(recovered, 0x1004, null, true);
        Check(recovered.IsConnected == true && recovered.IsReadingUnavailable && connectionTracker.MissedPing(recovered).IsConnected == null,
            "Errore della sola batteria viene confuso con mouse scollegato");
        LogitechBatteryProvider.ApplyBattery(offline, 0x1004, new byte[] { 89, 8, 0 }, true);
        offline.IsConnected = true;
        card.UpdateBattery(offline);
        Check(!offline.IsReadingUnavailable && offline.Percent == 89 && ((TextBlock)card.FindName("PowerStateText")).Text == "A batteria", "Ripresa della lettura non ripristina stato e percentuale");
        offline = LogitechBatteryProvider.Unavailable(mouse);
        var incomplete = new BatteryDeviceSnapshot { Id = mouse.Id, IsConnected = true };
        Check(LogitechBatteryProvider.Merge(new[] { incomplete }, new[] { offline }).Single() == offline, "Windows senza dati nasconde lo stato di timeout HID");
        var windows = new BatteryDeviceSnapshot { Id = mouse.Id.ToUpperInvariant(), Name = "Windows mouse", Percent = 80, IsConnected = true };
        var second = new BatteryDeviceSnapshot { Id = "other-mouse", Name = mouse.Name, Percent = 60, IsConnected = true };
        var merged = LogitechBatteryProvider.Merge(new[] { windows, second }, new[] { mouse });
        Check(merged.Count == 2 && merged.Contains(mouse) && merged.Contains(second) && mouse.Percent == 90 && mouse.IsCharging == true, "Windows e HID++ duplicano lo stesso contenitore o accorpano mouse distinti con nome identico");
        merged = LogitechBatteryProvider.Merge(new[] { windows }, new[] { offline });
        Check(merged.Single() == windows && windows.Percent == 80, "HID++ non disponibile nasconde dati Windows validi");
        var reply = new byte[] { 0x11, 1, 8, 0x1B, 90, 8, 0 };
        Check(DeskHardwareWidget.Logitech.HidTransport.Matches(reply, 1, 8, 0x1B), "Risposta HID++ corretta scartata");
        Check(!DeskHardwareWidget.Logitech.HidTransport.Matches(reply, 2, 8, 0x1B) && !DeskHardwareWidget.Logitech.HidTransport.Matches(reply, 1, 8, 0x1C), "Risposta HID++ di altro slot o client accettata");
        Check(!DeskHardwareWidget.Logitech.HidTransport.Matches(new byte[] { 0x11, 1, 8 }, 1, 8, 0x1B), "Pacchetto HID++ corto accettato");
        Check(DeskHardwareWidget.Logitech.HidTransport.Matches(new byte[] { 0x11, 1, 0xFF, 8, 0x1B, 4, 0 }, 1, 8, 0x1B), "Errore HID++ non correlato alla richiesta");
    }
    static void TestApplicationIcon()
    {
        var resource = Application.GetResourceStream(new Uri("/OpenWidget;component/Assets/App.ico", UriKind.Relative));
        using (var stream = resource.Stream)
        using (var icon = new System.Drawing.Icon(stream, 32, 32))
        using (var bitmap = icon.ToBitmap())
        {
            Check(icon.Width == 32 && icon.Height == 32, "Icona dell'area notifiche non caricabile alla dimensione nativa");
            bool hasCyan = false;
            for (int x = 0; x < bitmap.Width; x++)
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.R < 100 && color.G > 140 && color.B > 210) hasCyan = true;
                }
            Check(hasCyan, "Il logo con l'onda ciano è assente dall'icona condivisa");
        }
        using (var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(typeof(App).Assembly.Location))
            Check(executableIcon != null, "Icona dell'eseguibile non disponibile a Windows");
    }
    static void TestGpuVisibility(MainWindow window, HardwareSnapshot snapshot, FrameworkElement root)
    {
        var panel = (WrapPanel)window.FindName("CardsPanel");
        var originalCards = panel.Children.OfType<GpuCard>().ToDictionary(card => card.DeviceId);
        string firstId = snapshot.Gpus[0].Id, secondId = snapshot.Gpus[1].Id;
        Action<int> toggle = index => window.GetCommands().First(command => command != null && command.Label.StartsWith("Mostra GPU " + index + " ·")).Execute();
        toggle(1);
        Check(originalCards[firstId].Visibility == Visibility.Collapsed && originalCards[secondId].Visibility == Visibility.Visible, "Non è possibile mostrare soltanto la seconda GPU");
        Check(((Border)window.FindName("cardNetwork")).Width == 252, "Layout rete non aggiornato nascondendo una GPU");
        toggle(2);
        Check(originalCards.Values.All(card => card.Visibility == Visibility.Collapsed), "Non è possibile nascondere tutte le GPU");
        Check(((Border)window.FindName("cardNetwork")).Width == 512, "Layout rete non aggiornato nascondendo tutte le GPU");
        toggle(1);
        Check(originalCards[firstId].Visibility == Visibility.Visible && originalCards[secondId].Visibility == Visibility.Collapsed, "Non è possibile mostrare soltanto la prima GPU");
        Check(ConfigManager.Load().HiddenGpuIds.SequenceEqual(new[] { secondId }), "Selezione GPU del menu rapido non salvata");
        var copy = window.CurrentSettings;
        copy.HiddenGpuIds.Clear();
        Check(window.CurrentSettings.HiddenGpuIds.Contains(secondId), "La copia delle impostazioni condivide la lista GPU attiva");
        snapshot.Gpus.Reverse(); window.RenderSnapshot(snapshot); Layout(root);
        Check(originalCards[secondId].Visibility == Visibility.Collapsed, "La selezione GPU dipende dall'ordine di rilevamento");
        Check(panel.Children.OfType<GpuCard>().All(card => originalCards[card.DeviceId] == card), "Cambiare ordine GPU perde le schede o la cronologia");
        snapshot.Gpus.RemoveAt(0); window.RenderSnapshot(snapshot);
        snapshot.Gpus.Add(MakeIntel()); window.RenderSnapshot(snapshot);
        Check(panel.Children.OfType<GpuCard>().Single(card => card.DeviceId == secondId).Visibility == Visibility.Collapsed, "La GPU ricomparsa perde la preferenza di visibilità");
        snapshot.Gpus.Add(HardwareMonitor.CreateGpuSnapshot("/gpu-amd/new", "Nuova GPU", "GpuAmd", new List<SensorReading>()));
        window.RenderSnapshot(snapshot);
        Check(panel.Children.OfType<GpuCard>().Single(card => card.DeviceId == "/gpu-amd/new").Visibility == Visibility.Visible, "Una nuova GPU viene nascosta per errore");
        snapshot.Gpus.RemoveAt(2); window.RenderSnapshot(snapshot);
        var compact = window.GetCommands().First(command => command != null && command.Label.StartsWith("Modalità compatta"));
        compact.Execute();
        Check(panel.Children.OfType<GpuCard>().Single(card => card.DeviceId == secondId).Visibility == Visibility.Collapsed, "La modalità compatta riattiva una GPU nascosta");
        compact.Execute(); toggle(2); Layout(root);
        Check(panel.Children.OfType<GpuCard>().All(card => card.Visibility == Visibility.Visible), "Ripristino di tutte le GPU fallito");
    }
    static void TestLiveWindow(App application)
    {
        var live = new MainWindow { Opacity = 0 };
        application.MainWindow = live;
        var frame = new DispatcherFrame();
        bool closed = false;
        Exception failure = null;
        live.Closed += delegate { closed = true; frame.Continue = false; };
        live.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += delegate
        {
            timer.Stop();
            try
            {
                var panel = (WrapPanel)live.FindName("CardsPanel");
                Check(live.LatestSnapshot != null, "Campionamento in background non aggiorna la finestra reale");
                Check(panel.Children.OfType<GpuCard>().Count() == live.LatestSnapshot.Gpus.Count, "Schede GPU incoerenti con il campione rilevato");
                Check(live.ActualWidth > 0 && live.ActualHeight > 0, "Dimensioni finestra reale invalide");
                IntPtr handle = new WindowInteropHelper(live).Handle;
                Win32Helper.SetClickThrough(handle, true);
                Check((Win32Helper.GetWindowLongPtr(handle, Win32Helper.GWL_EXSTYLE).ToInt64() & Win32Helper.WS_EX_TRANSPARENT) != 0, "Click-through Win32 non applicato");
                Win32Helper.SetClickThrough(handle, false);
                Check((Win32Helper.GetWindowLongPtr(handle, Win32Helper.GWL_EXSTYLE).ToInt64() & Win32Helper.WS_EX_TRANSPARENT) == 0, "Click-through Win32 non disattivato");
                TestManagementWindow(live);
                TestBatteryWindow(live);
            }
            catch (Exception ex) { failure = ex; }
            live.Close();
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
        Check(closed, "Chiusura della finestra e dei sensori incompleta");
        Check(!application.Windows.OfType<SettingsWindow>().Any(), "Pannello rimasto aperto dopo l'uscita dal widget");
        Check(!application.Windows.OfType<BatteryWindow>().Any(), "Widget batterie rimasto aperto dopo l'uscita dall'app");
    }
    static void TestManagementWindow(MainWindow live)
    {
        // Hosted Windows CI may have no GPU. Exercise card controls with the
        // same synthetic sensor data used by the rendering regression checks.
        if (live.LatestSnapshot.Gpus.Count == 0)
        {
            live.LatestSnapshot.Gpus.Add(MakeNvidia(48.125));
            live.LatestSnapshot.Gpus.Add(MakeIntel());
            live.RenderSnapshot(live.LatestSnapshot);
        }
        var settings = live.CurrentSettings;
        settings.X = -12345;
        Check(live.CurrentSettings.X != -12345, "Il pannello può modificare le impostazioni prima di Applica");
        live.GetCommands().First(command => command != null && command.Label.StartsWith("Gestisci widget")).Execute();
        var management = Application.Current.Windows.OfType<SettingsWindow>().Single();
        management.Opacity = 0;
        Check(management.Icon != null && live.Icon != null, "Le finestre non usano l'icona dell'app");
        Check(((Panel)((RadioButton)management.FindName("NavCards")).Parent).Children.OfType<RadioButton>().Select(item => (string)item.Tag)
            .SequenceEqual(new[] { "Cards", "Batteries", "Position", "General", "Diagnostics" }), "Ordine delle sezioni impostazioni errato");
        live.ShowSettings();
        Check(Application.Current.Windows.OfType<SettingsWindow>().Count() == 1, "Il clic ripetuto crea più pannelli");
        management.WindowState = WindowState.Minimized;
        live.ShowSettings();
        Check(management.WindowState == WindowState.Normal, "Il pannello minimizzato non viene ripristinato");
        var refresh = (TextBox)management.FindName("RefreshInterval");
        refresh.Text = "249";
        Check(!management.ApplyChanges() && live.CurrentSettings.RefreshIntervalMs == 1000, "Intervallo invalido applicato o salvato");
        refresh.Text = "60001";
        Check(!management.ApplyChanges(), "Intervallo superiore a 60 secondi accettato");
        refresh.Text = "750";
        var x = (TextBox)management.FindName("PositionX");
        x.Text = "NaN";
        Check(!management.ApplyChanges(), "Coordinata NaN accettata dal pannello");
        x.Text = live.CurrentSettings.X.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var clickThrough = (CheckBox)management.FindName("ClickThrough");
        var locked = (CheckBox)management.FindName("LockPosition");
        clickThrough.IsChecked = true;
        Check(locked.IsChecked == true, "Click-through non blocca la posizione nel pannello");
        ((CheckBox)management.FindName("ShowMemory")).IsChecked = false;
        ((CheckBox)management.FindName("ShowNetwork")).IsChecked = true;
        Check(management.ApplyChanges(), "Applicazione delle impostazioni fallita");
        Check(live.CurrentSettings.RefreshIntervalMs == 750 && live.CurrentSettings.IsClickThrough && live.CurrentSettings.IsLocked,
            "Intervallo o modalità interazione non aggiornati");
        Check(((Border)live.FindName("cardMemory")).Visibility == Visibility.Collapsed && ((Border)live.FindName("cardNetwork")).Visibility == Visibility.Visible,
            "Visibilità delle singole schede non applicata");
        Check(ConfigManager.Load().RefreshIntervalMs == 750 && !ConfigManager.Load().ShowMemoryCard, "Impostazioni del pannello non persistenti");
        IntPtr handle = new WindowInteropHelper(live).Handle;
        Check((Win32Helper.GetWindowLongPtr(handle, Win32Helper.GWL_EXSTYLE).ToInt64() & Win32Helper.WS_EX_TRANSPARENT) != 0,
            "Click-through del pannello non applicato alla finestra del widget");
        IntPtr panelHandle = new WindowInteropHelper(management).Handle;
        Check((Win32Helper.GetWindowLongPtr(panelHandle, Win32Helper.GWL_EXSTYLE).ToInt64() & Win32Helper.WS_EX_TRANSPARENT) == 0,
            "Il pannello impostazioni lascia passare i clic");
        locked.IsChecked = false;
        Check(clickThrough.IsChecked == false, "Lo sblocco dal pannello non disattiva il click-through");
        Check(management.ApplyChanges(), "Disattivazione del click-through fallita");
        live.GetCommands().First(command => command != null && command.Label.Contains("posizione")).Execute();
        Check(locked.IsChecked == true, "Il pannello non segue i cambiamenti del menu rapido");
        var gpuOptions = (ItemsControl)management.FindName("GpuOptions");
        var options = gpuOptions.Items.Cast<SettingsWindow.GpuVisibilityOption>().ToList();
        Check(options.Count == live.LatestSnapshot.Gpus.Count, "Il pannello non elenca separatamente tutte le GPU");
        options[0].IsVisible = false;
        live.RenderSnapshot(live.LatestSnapshot);
        Check(!options[0].IsVisible, "Un nuovo campione annulla la selezione GPU in sospeso");
        Check(management.ApplyChanges() && live.CurrentSettings.HiddenGpuIds.Contains(options[0].Id), "La selezione GPU del pannello non viene applicata");
        live.GetCommands().First(command => command != null && command.Label.StartsWith("Mostra GPU 1 ·")).Execute();
        Check(options[0].IsVisible, "Il pannello non segue la selezione GPU del menu rapido");
        ((Button)management.FindName("HideAllGpusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(management.ApplyChanges() && ((WrapPanel)live.FindName("CardsPanel")).Children.OfType<GpuCard>().All(card => card.Visibility == Visibility.Collapsed), "Nascondi tutte non applicato");
        ((Button)management.FindName("ShowAllGpusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(management.ApplyChanges() && live.CurrentSettings.HiddenGpuIds.Count == 0, "Mostra tutte non applicato");
        var battery = Application.Current.Windows.OfType<BatteryWindow>().Single();
        var batteryX = (TextBox)management.FindName("BatteryPositionX");
        var batteryY = (TextBox)management.FindName("BatteryPositionY");
        double originalMainX = live.Left, originalMainY = live.Top;
        var centeredBattery = live.CenteredBatteryPosition;
        batteryX.Text = "NaN";
        Check(!management.ApplyChanges(), "Coordinata batterie non valida accettata dal pannello Posizione");
        batteryX.Text = centeredBattery.X.ToString(System.Globalization.CultureInfo.CurrentCulture);
        batteryY.Text = centeredBattery.Y.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Check(management.ApplyChanges() && Math.Abs(battery.Left - centeredBattery.X) < 0.01 && Math.Abs(battery.Top - centeredBattery.Y) < 0.01 &&
            live.Left == originalMainX && live.Top == originalMainY, "Spostare le batterie dal pannello sposta anche le temperature");
        Check(ConfigManager.Load().BatteryPositionSet && Math.Abs(ConfigManager.Load().BatteryX - centeredBattery.X) < 0.01,
            "Coordinate batterie del pannello non persistenti");
        ((TextBox)management.FindName("PositionX")).Text = (originalMainX + 20).ToString(System.Globalization.CultureInfo.CurrentCulture);
        Check(management.ApplyChanges() && live.Left == originalMainX + 20 && Math.Abs(battery.Left - centeredBattery.X) < 0.01,
            "Spostare le temperature dal pannello sposta anche le batterie");
        ((TextBox)management.FindName("PositionX")).Text = originalMainX.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Check(management.ApplyChanges(), "Ripristino della posizione temperature fallito");
        live.SetBatteryWidgetVisible(false);
        batteryX.Text = (centeredBattery.X + 10).ToString(System.Globalization.CultureInfo.CurrentCulture);
        Check(management.ApplyChanges() && !battery.IsVisible && Math.Abs(live.CurrentSettings.BatteryX - centeredBattery.X - 10) < 0.01,
            "Modificare la posizione delle batterie nascoste le rende visibili o perde le coordinate");
        live.SetBatteryWidgetVisible(true);
        var navigation = ((Panel)((RadioButton)management.FindName("NavCards")).Parent).Children.OfType<RadioButton>();
        foreach (var item in navigation)
        {
            item.IsChecked = true;
            management.UpdateLayout();
            SaveWindowPreview(management, "settings-" + item.Tag + ".png");
        }
        management.Width = 840; management.Height = 600; management.UpdateLayout();
        SaveWindowPreview(management, "settings-small.png");
        management.Close();
        Check(live.IsVisible, "Chiudere il pannello chiude anche il widget");
        live.ShowSettings();
        management = Application.Current.Windows.OfType<SettingsWindow>().Single();
        management.Opacity = 0;
        Check(((TextBox)management.FindName("RefreshInterval")).Text == "750", "Riapertura del pannello perde le impostazioni");
    }
    static void TestBatteryWindow(MainWindow live)
    {
        var battery = Application.Current.Windows.OfType<BatteryWindow>().Single();
        var original = live.CurrentSettings;
        var native = live.LatestBatterySnapshot;
        Check(battery.IsVisible && new WindowInteropHelper(battery).Handle != new WindowInteropHelper(live).Handle, "Il widget batterie non è una finestra indipendente");
        Check(native != null, "Il campionamento batterie non aggiorna la finestra reale");
        SaveWindowPreview(battery, "battery-live.png");
        var snapshot = new BatterySnapshot { Devices = new List<BatteryDeviceSnapshot> {
            new BatteryDeviceSnapshot { Id = "pc", Name = "Questo PC", Kind = "Laptop", IsPc = true, Percent = 81, IsOnAc = true, IsCharging = false, IsConnected = true },
            new BatteryDeviceSnapshot { Id = "mouse", Name = "MX Master 3S", Kind = "Mouse", IsBluetooth = true, Percent = 90, IsConnected = true },
            new BatteryDeviceSnapshot { Id = "headset", Name = "Auricolari", Kind = "Headphones", IsBluetooth = true, Percent = 64, IsCharging = true, IsConnected = true },
            new BatteryDeviceSnapshot { Id = "keyboard", Name = "Tastiera", Kind = "Keyboard", IsBluetooth = true, Percent = 20, IsConnected = true }
        } };
        live.RenderBatterySnapshot(snapshot);
        Check(((WrapPanel)battery.FindName("BatteryCards")).Children.Count == 4, "Il widget non mostra tutti i dispositivi");
        SaveWindowPreview(battery, "battery-demo.png");
        snapshot.Devices.Add(new BatteryDeviceSnapshot { Id = "phone", Name = "Telefono", Kind = "Phone", Percent = 55, IsConnected = true });
        live.RenderBatterySnapshot(snapshot);
        var batterySlots = (WrapPanel)battery.FindName("BatteryCards");
        Check(batterySlots.Children.Count == 8 && batterySlots.Children.OfType<BatteryCard>().Count(card => !card.IsPlaceholder) == 5,
            "I dispositivi oltre i quattro spazi iniziali non sono visibili nella seconda riga");
        snapshot.Devices.RemoveAt(4); live.RenderBatterySnapshot(snapshot);
        var next = live.CurrentSettings;
        next.BatteryClickThrough = true; next.BatteryIsLocked = false;
        string error;
        Check(live.ApplySettings(next, out error), "Impostazioni batterie non applicate");
        Check(live.CurrentSettings.BatteryIsLocked && !live.CurrentSettings.IsClickThrough, "Il click-through batterie modifica il widget hardware");
        Check((Win32Helper.GetWindowLongPtr(new WindowInteropHelper(battery).Handle, Win32Helper.GWL_EXSTYLE).ToInt64() & Win32Helper.WS_EX_TRANSPARENT) != 0,
            "Click-through batterie non applicato alla seconda finestra");
        var batteryClickCommand = live.GetCommands().First(command => command != null && command.Label == "Click-through widget batterie");
        Check(batteryClickCommand.Checked, "Il menu rapido non indica il click-through batterie attivo");
        batteryClickCommand.Execute();
        Check(!live.CurrentSettings.BatteryClickThrough && !live.CurrentSettings.IsClickThrough &&
            (Win32Helper.GetWindowLongPtr(new WindowInteropHelper(battery).Handle, Win32Helper.GWL_EXSTYLE).ToInt64() & Win32Helper.WS_EX_TRANSPARENT) == 0,
            "Non è possibile disattivare il click-through batterie dal menu dell'area notifiche");
        batteryClickCommand.Execute();
        Check(live.CurrentSettings.BatteryClickThrough && ConfigManager.Load().BatteryClickThrough, "Click-through batterie non riattivato o non salvato dal menu rapido");
        next = live.CurrentSettings; next.BatteryShowAccessories = false;
        Check(live.ApplySettings(next, out error) && batterySlots.Children.Count == 4 && batterySlots.Children.OfType<BatteryCard>().Count(card => !card.IsPlaceholder) == 1,
            "Il filtro accessori modifica anche la batteria PC o rimuove gli spazi liberi");
        next = live.CurrentSettings; next.BatteryShowPc = false;
        Check(live.ApplySettings(next, out error) && batterySlots.Children.Count == 4 && batterySlots.Children.OfType<BatteryCard>().All(card => card.IsPlaceholder),
            "Lo stato senza dispositivi non conserva quattro cerchi vuoti");
        SaveWindowPreview(battery, "battery-empty.png");
        live.SetBatteryWidgetVisible(false);
        Check(!battery.IsVisible && live.IsVisible, "Nascondere le batterie nasconde anche il widget hardware");
        live.SetBatteryWidgetVisible(true);
        Check(battery.IsVisible && Application.Current.Windows.OfType<BatteryWindow>().Count() == 1, "Riapertura batterie crea finestre duplicate");
        live.SaveBatteryPosition(battery.Left, battery.Top);
        var saved = ConfigManager.Load();
        Check(saved.BatteryPositionSet && saved.BatteryX == battery.Left && saved.BatteryY == battery.Top && saved.X == live.Left,
            "Posizione batterie non salvata separatamente");
        Check(live.ApplySettings(original, out error), "Ripristino delle impostazioni batterie fallito");
        snapshot.Devices[1].Percent = null; live.RenderBatterySnapshot(snapshot);
        SaveWindowPreview(battery, "battery-unknown.png");
        live.ShowBatterySettings();
        var management = Application.Current.Windows.OfType<SettingsWindow>().Single();
        Check(((StackPanel)management.FindName("BatteriesPage")).Visibility == Visibility.Visible, "Il menu batterie non apre la sezione corretta");
        var interval = (TextBox)management.FindName("BatteryRefreshInterval");
        interval.Text = "8";
        Check(!management.ApplyChanges() && live.CurrentSettings.BatteryRefreshSeconds == 30, "Intervallo batterie non valido accettato");
        interval.Text = "30";
        Check(management.ApplyChanges(), "Intervallo batterie valido non applicato");
        live.RenderBatterySnapshot(native);
        ((ScrollViewer)management.FindName("PageScroll")).ScrollToTop();
        SaveWindowPreview(management, "settings-Batteries.png");
    }
    static void SaveWindowPreview(Window window, string filename)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename))) encoder.Save(stream);
    }
    static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
    }
}
