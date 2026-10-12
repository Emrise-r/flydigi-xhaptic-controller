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
    // ---------------------------------------------------------------- Audio source overrides (scope -> original enabled flag)
    static class SourceOverrides
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "sources.txt");
        public static Dictionary<string, bool> Load()
        {
            var d = new Dictionary<string, bool>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim() == "true";
                    }
            }
            catch { }
            return d;
        }
        public static void Save(Dictionary<string, bool> d)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + (kv.Value ? "true" : "false"));
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex) { Log.Write("cannot save sources.txt: " + ex.Message); }
        }
    }
}
