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
    static class UsbDevices
    {
        // Is the controller itself enumerated over USB (cable or charging dock)? The engine reports the controller's
        // vendor/product id; Windows keeps every USB instance it ever saw under Enum\USB\VID_xxxx&PID_xxxx, and
        // CM_Locate_DevNode only succeeds for instances that are present right now.
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);

        public static bool IsPresent(string vidPid)
        {
            if (string.IsNullOrEmpty(vidPid)) return false;
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\" + vidPid))
                {
                    if (key == null) return false;
                    foreach (var inst in key.GetSubKeyNames())
                    {
                        int dev;
                        if (CM_Locate_DevNodeW(out dev, @"USB\" + vidPid + @"\" + inst, 0) == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

    }
}
