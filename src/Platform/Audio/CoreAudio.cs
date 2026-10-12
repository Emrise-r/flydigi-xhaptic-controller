using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FlydigiHapticsGuard
{
    // Small helpers around the Core Audio COM objects: find playback devices, activate an interface on a
    // device, release COM references.
    static class CoreAudio
    {
        // The Windows default playback device, or null. The caller releases it.
        public static IMMDevice DefaultRenderDevice()
        {
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice device;
                return enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device) == HResult.S_OK ? device : null;
            }
            catch { return null; }
            finally { Release(enumerator); }
        }

        // Every active playback device. Each one is released when the loop moves on to the next.
        public static IEnumerable<IMMDevice> ActiveRenderDevices()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDeviceCollection devices = null;
            try
            {
                if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out devices) != HResult.S_OK || devices == null) yield break;
                int count;
                devices.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (devices.Item(i, out device) != HResult.S_OK || device == null) continue;
                    try { yield return device; }
                    finally { Release(device); }
                }
            }
            finally
            {
                Release(devices);
                Release(enumerator);
            }
        }

        // Activates interface T on a device, or returns null. The caller releases it.
        public static T Activate<T>(IMMDevice device) where T : class
        {
            Guid iid = typeof(T).GUID;
            object obj;
            if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out obj) != HResult.S_OK) return null;
            var result = obj as T;
            if (result == null) Release(obj);
            return result;
        }

        public static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
    }
}
