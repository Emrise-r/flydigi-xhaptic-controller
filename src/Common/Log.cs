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
    // ---------------------------------------------------------------- Logging
    // guard.log is rotated to guard.log.old when it exceeds log.maxFileKB (default 5 MB), so the two files
    // together never take more than about twice that.
    static class Log
    {
        static readonly object gate = new object();
        public static readonly string Dir = AppPaths.DataDir;
        public static readonly string FilePath = Path.Combine(Dir, "guard.log");
        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > (long)Settings.LogMaxFileKB * 1024) { File.Copy(FilePath, FilePath + ".old", true); File.Delete(FilePath); }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
