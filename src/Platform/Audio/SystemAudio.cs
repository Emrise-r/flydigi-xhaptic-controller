using System;
using System.Runtime.InteropServices;

namespace FlydigiHapticsGuard
{
    // The Windows default playback device: id, peak level, exclusive-mode probe.
    static class SystemAudio
    {
        // Returns false when there is no default playback device.
        public static bool Read(out string deviceId, out float peak)
        {
            deviceId = null; peak = 0;
            IMMDevice device = null; IAudioMeterInformation meter = null;
            try
            {
                device = CoreAudio.DefaultRenderDevice();
                if (device == null) return false;
                device.GetId(out deviceId);
                meter = CoreAudio.Activate<IAudioMeterInformation>(device);
                if (meter != null) meter.GetPeakValue(out peak);
                return true;
            }
            catch { return deviceId != null; }
            finally
            {
                CoreAudio.Release(meter);
                CoreAudio.Release(device);
            }
        }

        // Is the default playback device held by another app in exclusive mode (WASAPI exclusive / ASIO
        // players such as TIDAL "Exclusive Mode")? Probes by opening a shared stream (never started) and
        // closing it again. Returns null when it cannot tell.
        public static bool? DefaultDeviceInExclusiveUse()
        {
            IMMDevice device = null; IAudioClient client = null; IntPtr format = IntPtr.Zero;
            try
            {
                device = CoreAudio.DefaultRenderDevice();
                if (device == null) return null;
                client = CoreAudio.Activate<IAudioClient>(device);
                if (client == null) return null;

                int hr = client.GetMixFormat(out format);
                if (hr == HResult.S_OK) hr = client.Initialize(AudioClientShareMode.Shared, 0, ReferenceTime.OneSecond, 0, format, IntPtr.Zero);

                if (hr == HResult.S_OK) return false;
                if (hr == HResult.AUDCLNT_E_DEVICE_IN_USE) return true;
                return null;
            }
            catch { return null; }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                CoreAudio.Release(client);
                CoreAudio.Release(device);
            }
        }
    }
}
