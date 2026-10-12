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
    // ---------------------------------------------------------------- Persistent master switch
    // Line 1: "on"/"off". Line 2: the X-Haptics scopes the guard disabled, so "on" restores exactly those.
    static class AutoFlag
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "auto-mode.txt");
        static string[] Lines()
        {
            try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0]; }
            catch { return new string[0]; }
        }
        public static bool Load()
        {
            // on unless the user switched it off
            var l = Lines();
            return l.Length == 0 || l[0].Trim() != "off";
        }
        public static List<string> LoadScopes()
        {
            var l = Lines();
            var scopes = new List<string>();
            if (l.Length > 1)
                foreach (var s in l[1].Split(','))
                    if (s.Trim().Length > 0) scopes.Add(s.Trim());
            return scopes;
        }
        public static void Save(bool on, IEnumerable<string> disabledScopes)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllLines(FilePath, new[] { on ? "on" : "off", disabledScopes == null ? "" : string.Join(",", disabledScopes) });
            }
            catch (Exception ex) { Log.Write("cannot save switch state: " + ex.Message); }
        }
    }
}
