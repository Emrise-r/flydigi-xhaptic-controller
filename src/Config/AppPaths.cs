using System;
using System.IO;
using System.Windows.Forms;

namespace FlydigiHapticsGuard
{
    // config.json and data\ (state + log) live next to the exe, so the folder is self-contained. When the exe
    // folder is not writable (e.g. C:\Program Files without admin rights) they move to
    // %LOCALAPPDATA%\FlydigiHapticsGuard instead. The shipped defaults are always read from the exe folder.
    static class AppPaths
    {
        public static readonly string BaseDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static readonly string DefaultConfigPath = Path.Combine(BaseDir, "config", "config.default.json");   // shipped defaults
        public static readonly bool UsingFallback = !IsWritable(BaseDir);
        public static readonly string UserDir = UsingFallback
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlydigiHapticsGuard")
            : BaseDir;
        public static readonly string ConfigPath = Path.Combine(UserDir, "config.json");   // personal, not tracked
        public static readonly string DataDir = Path.Combine(UserDir, "data");

        static AppPaths()
        {
            // the fallback folder does not exist on first run
            try { Directory.CreateDirectory(UserDir); } catch { }
        }

        static bool IsWritable(string dir)
        {
            // create (and immediately delete) a temp file
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
