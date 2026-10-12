using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FlydigiHapticsGuard
{
    // ---------------------------------------------------------------- Start with Windows (tray toggle, off unless the user turns it on)
    // Uses HKCU\...\Run. Older versions used a Startup-folder shortcut; it counts as "on" and is removed on the next toggle.
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "X-Haptics Guard";
        static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }
        static string LegacyShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "X-Haptics Guard.lnk"); } }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = key == null ? null : key.GetValue(ValueName) as string;
                    if (v != null && string.Equals(v.Trim(), Command, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return File.Exists(LegacyShortcut);
        }

        public static void Set(bool on)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
            if (File.Exists(LegacyShortcut)) File.Delete(LegacyShortcut);
        }
    }
}
