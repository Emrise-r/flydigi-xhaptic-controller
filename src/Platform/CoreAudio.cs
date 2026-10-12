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
    // ---------------------------------------------------------------- Core Audio (default endpoint id + peak meter)
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }


    // Processes that own an audio session on any active playback device (what the Volume mixer shows).
    static class AudioSessions
    {
        const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, AudioSessionStateExpired = 2;

        public static HashSet<int> ProcessIds() { return new HashSet<int>(SessionPeaks().Keys); }

        // pid -> current peak level of its audio session(s), over all active playback devices
        public static Dictionary<int, float> SessionPeaks()
        {
            var pids = new Dictionary<int, float>();
            IMMDeviceEnumerator en = null; IMMDeviceCollection devices = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out devices) != 0 || devices == null) return pids;
                int count; devices.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev = null; object mgrObj = null; IAudioSessionEnumerator sessions = null;
                    try
                    {
                        if (devices.Item(i, out dev) != 0 || dev == null) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) continue;
                        if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                        int n; sessions.GetCount(out n);
                        for (int s = 0; s < n; s++)
                        {
                            object ctlObj = null;
                            try
                            {
                                if (sessions.GetSession(s, out ctlObj) != 0 || ctlObj == null) continue;
                                var ctl = (IAudioSessionControl2)ctlObj;
                                int state; uint pid;
                                if (ctl.GetState(out state) == 0 && state != AudioSessionStateExpired && ctl.GetProcessId(out pid) == 0 && pid != 0)
                                {
                                    float peak = 0, old;
                                    var meter = ctlObj as IAudioMeterInformation;
                                    if (meter != null) meter.GetPeakValue(out peak);
                                    pids[(int)pid] = pids.TryGetValue((int)pid, out old) ? Math.Max(old, peak) : peak;
                                }
                            }
                            catch { }
                            finally { if (ctlObj != null) Marshal.ReleaseComObject(ctlObj); }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (sessions != null) Marshal.ReleaseComObject(sessions);
                        if (mgrObj != null) Marshal.ReleaseComObject(mgrObj);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            catch { }
            finally
            {
                if (devices != null) Marshal.ReleaseComObject(devices);
                if (en != null) Marshal.ReleaseComObject(en);
            }
            return pids;
        }

    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
    }

    static class SystemAudio
    {
        const int eRender = 0, eConsole = 0, CLSCTX_ALL = 23;

        // Returns false when there is no default playback device.
        const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);

        // Is the default playback device held by another app in exclusive mode (WASAPI exclusive / ASIO
        // players such as TIDAL "Exclusive Mode")? Probes by opening a shared stream (never started) and
        // closing it again. Returns null when it cannot tell.
        public static bool? DefaultDeviceInExclusiveUse()
        {
            IMMDeviceEnumerator en = null; IMMDevice dev = null; object clientObj = null; IntPtr format = IntPtr.Zero;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(eRender, eConsole, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioClient).GUID;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out clientObj) != 0 || clientObj == null) return null;
                var client = (IAudioClient)clientObj;
                int hr = client.GetMixFormat(out format);
                if (hr == AUDCLNT_E_DEVICE_IN_USE) return true;
                if (hr != 0) return null;
                hr = client.Initialize(0 /* shared */, 0, 10000000 /* 1 s */, 0, format, IntPtr.Zero);
                if (hr == 0) return false;
                if (hr == AUDCLNT_E_DEVICE_IN_USE) return true;
                return null;
            }
            catch { return null; }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                if (clientObj != null) Marshal.ReleaseComObject(clientObj);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        public static bool Read(out string deviceId, out float peak)
        {
            deviceId = null; peak = 0;
            IMMDeviceEnumerator en = null; IMMDevice dev = null; object meterObj = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(eRender, eConsole, out dev) != 0 || dev == null) return false;
                dev.GetId(out deviceId);
                Guid iid = typeof(IAudioMeterInformation).GUID;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out meterObj) == 0 && meterObj != null)
                    ((IAudioMeterInformation)meterObj).GetPeakValue(out peak);
                return true;
            }
            catch { return deviceId != null; }
            finally
            {
                if (meterObj != null) Marshal.ReleaseComObject(meterObj);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }
    }
}
