using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlydigiHapticsGuard
{
    // Audio source overrides (data\sources.txt): scope -> original enabled flag
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
                File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + (kv.Value ? "true" : "false")));
            }
            catch (Exception ex) { Log.Write("cannot save sources.txt: " + ex.Message); }
        }
    }
}
