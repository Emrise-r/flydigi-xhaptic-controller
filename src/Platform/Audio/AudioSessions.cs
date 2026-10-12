using System.Collections.Generic;

namespace FlydigiHapticsGuard
{
    // Processes that own an audio session on any active playback device (what the Volume mixer shows).
    static class AudioSessions
    {
        public static HashSet<int> ProcessIds() { return new HashSet<int>(SessionPeaks().Keys); }

        // pid -> highest current peak level of its audio sessions, over all active playback devices
        public static Dictionary<int, float> SessionPeaks()
        {
            var peaks = new Dictionary<int, float>();
            try
            {
                foreach (var device in CoreAudio.ActiveRenderDevices())
                    AddDevicePeaks(device, peaks);
            }
            catch { }
            return peaks;
        }

        // A device that fails is skipped; the other devices are still read.
        static void AddDevicePeaks(IMMDevice device, Dictionary<int, float> peaks)
        {
            try
            {
                foreach (var session in Sessions(device))
                {
                    int pid; float peak, old;
                    if (!TryReadSession(session, out pid, out peak)) continue;
                    if (!peaks.TryGetValue(pid, out old) || peak > old) peaks[pid] = peak;
                }
            }
            catch { }
        }

        // The device's audio sessions. Each one is released when the loop moves on to the next.
        static IEnumerable<object> Sessions(IMMDevice device)
        {
            var manager = CoreAudio.Activate<IAudioSessionManager2>(device);
            IAudioSessionEnumerator sessions = null;
            try
            {
                if (manager == null || manager.GetSessionEnumerator(out sessions) != HResult.S_OK || sessions == null) yield break;
                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    object session;
                    if (sessions.GetSession(i, out session) != HResult.S_OK || session == null) continue;
                    try { yield return session; }
                    finally { CoreAudio.Release(session); }
                }
            }
            finally
            {
                CoreAudio.Release(sessions);
                CoreAudio.Release(manager);
            }
        }

        // False for expired sessions, the system sounds session (PID 0) and sessions shared by several processes.
        static bool TryReadSession(object session, out int pid, out float peak)
        {
            pid = 0; peak = 0;
            try
            {
                var control = (IAudioSessionControl2)session;
                AudioSessionState state; uint id;
                if (control.GetState(out state) != HResult.S_OK || state == AudioSessionState.Expired) return false;
                if (control.GetProcessId(out id) != HResult.S_OK || id == 0) return false;

                var meter = session as IAudioMeterInformation;
                if (meter != null) meter.GetPeakValue(out peak);
                pid = (int)id;
                return true;
            }
            catch { return false; }
        }
    }
}
