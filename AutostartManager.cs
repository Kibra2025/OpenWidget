using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using Microsoft.Win32;

namespace DeskHardwareWidget
{
    internal static class AutostartManager
    {
        internal const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        internal const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "DeskHardwareWidget";
        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonInteractiveToken = 3;

        private static string UserSid
        {
            get { using (var identity = WindowsIdentity.GetCurrent()) return identity.User.Value; }
        }
        internal static string TaskName { get { return AppName + "-" + UserSid; } }
        private static string ExecutablePath { get { return Assembly.GetExecutingAssembly().Location; } }

        // The task belongs to the current user, uses their interactive desktop and
        // highest token, and never stores a password or runs the widget as SYSTEM.
        internal static string BuildTaskXml(string executablePath, string userSid)
        {
            executablePath = Path.GetFullPath(executablePath);
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(@"<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>DeskHardwareWidget: avvio come amministratore all'accesso dell'utente.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId /></LogonTrigger></Triggers>
  <Principals><Principal id=""WidgetUser""><UserId /><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""WidgetUser""><Exec><Command /><Arguments>--autostart</Arguments><WorkingDirectory /></Exec></Actions>
</Task>");
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("t", TaskNamespace);
            document.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger/t:UserId", namespaces).InnerText = userSid;
            document.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:UserId", namespaces).InnerText = userSid;
            document.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Command", namespaces).InnerText = executablePath;
            document.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:WorkingDirectory", namespaces).InnerText = Path.GetDirectoryName(executablePath);
            return document.OuterXml;
        }

        internal static bool TaskMatches(string xml, string executablePath, string userSid)
        {
            var expected = new XmlDocument { XmlResolver = null };
            expected.LoadXml(BuildTaskXml(executablePath, userSid));
            var actual = new XmlDocument { XmlResolver = null };
            actual.LoadXml(xml);
            var namespaces = new XmlNamespaceManager(actual.NameTable);
            namespaces.AddNamespace("t", TaskNamespace);
            // Scheduler adds defaults and reformats the XML: compare the fields we own.
            string[] paths = {
                "Triggers/LogonTrigger/Enabled", "Triggers/LogonTrigger/UserId",
                "Principals/Principal/UserId", "Principals/Principal/LogonType", "Principals/Principal/RunLevel",
                "Settings/MultipleInstancesPolicy", "Settings/DisallowStartIfOnBatteries", "Settings/StopIfGoingOnBatteries",
                "Settings/StartWhenAvailable", "Settings/RunOnlyIfNetworkAvailable", "Settings/AllowStartOnDemand",
                "Settings/Enabled", "Settings/RunOnlyIfIdle", "Settings/ExecutionTimeLimit", "Settings/Priority",
                "Actions/Exec/Command", "Actions/Exec/Arguments", "Actions/Exec/WorkingDirectory"
            };
            // Persisted tasks omit schema defaults and may resolve a logon SID
            // to DOMAIN\user. Compare the effective values and user identity.
            var defaults = new Dictionary<string, string> {
                { "Triggers/LogonTrigger/Enabled", "true" },
                { "Settings/RunOnlyIfNetworkAvailable", "false" },
                { "Settings/AllowStartOnDemand", "true" },
                { "Settings/Enabled", "true" },
                { "Settings/RunOnlyIfIdle", "false" },
                { "Settings/Priority", "7" }
            };
            foreach (string path in paths)
            {
                string query = "/t:Task/t:" + path.Replace("/", "/t:");
                var node = actual.SelectSingleNode(query, namespaces);
                var desired = expected.SelectSingleNode(query, namespaces);
                string value;
                if (node != null) value = node.InnerText;
                else if (!defaults.TryGetValue(path, out value)) return false;
                if (path.EndsWith("/UserId", StringComparison.Ordinal))
                {
                    if (!SameUser(value, userSid)) return false;
                }
                else if (!string.Equals(value, desired.InnerText, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return actual.SelectNodes("/t:Task/t:Triggers/*", namespaces).Count == 1 &&
                actual.SelectNodes("/t:Task/t:Principals/*", namespaces).Count == 1 &&
                actual.SelectNodes("/t:Task/t:Actions/*", namespaces).Count == 1;
        }

        private static bool SameUser(string value, string sid)
        {
            if (string.Equals(value, sid, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(value)) return false;
            try { return new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value == sid; }
            catch (IdentityNotMappedException) { return false; }
        }

        internal static dynamic FindTask(dynamic folder, string taskName)
        {
            try { return folder.GetTask(taskName); }
            // The dynamic COM binder can translate these HRESULTs into IO
            // exceptions instead of preserving COMException on .NET Framework.
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (COMException ex)
            {
                if (ex.ErrorCode == unchecked((int)0x80070002) || ex.ErrorCode == unchecked((int)0x80070003)) return null;
                throw;
            }
        }
        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        internal static bool HasLegacyAutostart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey))
                    return key != null && !string.IsNullOrWhiteSpace(key.GetValue(AppName) as string);
            }
            catch (Exception ex) { Diagnostics.Write("Lettura dell'avvio automatico precedente fallita", ex); return false; }
        }

        internal static bool IsEnabled()
        {
            dynamic service = null, folder = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                service.Connect();
                folder = service.GetFolder(@"\");
                task = FindTask(folder, TaskName);
                return task != null && task.Enabled && TaskMatches((string)task.Xml, ExecutablePath, UserSid);
            }
            catch (Exception ex) { Diagnostics.Write("Lettura dell'attività di avvio automatico fallita", ex); return false; }
            finally { Release((object)task); Release((object)folder); Release((object)service); }
        }

        internal static bool SetEnabled(bool enable)
        {
            dynamic service = null, folder = null, task = null;
            try
            {
                if (!Win32Helper.IsRunningAsAdministrator())
                    throw new UnauthorizedAccessException("L'avvio automatico elevato richiede privilegi di amministratore.");
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                service.Connect();
                folder = service.GetFolder(@"\");
                task = FindTask(folder, TaskName);
                if (enable)
                {
                    string sid = UserSid;
                    string executable = ExecutablePath;
                    if (task == null || !task.Enabled || !TaskMatches((string)task.Xml, executable, sid))
                    {
                        Release((object)task); task = null;
                        task = folder.RegisterTask(TaskName, BuildTaskXml(executable, sid), TaskCreateOrUpdate,
                            sid, null, TaskLogonInteractiveToken, null);
                    }
                    if (!task.Enabled || !TaskMatches((string)task.Xml, executable, sid))
                        throw new InvalidOperationException("L'attività registrata non corrisponde all'avvio elevato richiesto.");
                }
                else if (task != null) folder.DeleteTask(TaskName, 0);

                // Remove Run only after successful registration, so a migration
                // failure does not discard the user's previous startup setting.
                using (var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true))
                    if (key != null) key.DeleteValue(AppName, false);
                return true;
            }
            catch (Exception ex) { Diagnostics.Write("Aggiornamento dell'avvio automatico come amministratore fallito", ex); return false; }
            finally { Release((object)task); Release((object)folder); Release((object)service); }
        }
    }
}
