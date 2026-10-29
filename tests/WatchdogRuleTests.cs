using System;
using Poll = FlydigiHapticsGuard.Guard.Poll;

namespace FlydigiHapticsGuard.Tests
{
    // The re-arm rules with made-up engine states; no engine is contacted (none of these rules calls it).
    static class WatchdogRuleTests
    {
        static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0);

        static Guard NewGuard()
        {
            Sandbox.DefaultSettings();
            return new Guard(message => { }, true);
        }

        // a healthy engine on system capture (Mix), controller awake, nothing playing
        static XHapticsStatus Healthy()
        {
            return new XHapticsStatus
            {
                SessionState = "Running", Scope = "global", CaptureRunning = true, OutputRunning = true,
                CaptureError = "", OutputError = "", CaptureSource = "system", CapturePid = 0, FrameSeq = 1,
                HidConnected = true, HidOpened = true, HidError = "", GripGateOpen = true,
            };
        }

        static Poll At(int second, XHapticsStatus st, float systemPeak = 0, bool awake = true)
        {
            return new Poll
            {
                Now = T0.AddSeconds(second), St = st, HaveDevice = true, DeviceId = "dev", SystemPeak = systemPeak,
                Idle = st.Idle || (!st.CaptureRunning && !st.OutputRunning), Awake = awake, SinceKick = 1000,
            };
        }

        [Test]
        static void Healthy_engine_breaks_no_rule()
        {
            var g = NewGuard();
            for (int s = 0; s < 20; s++)
            {
                var p = At(s, Healthy());
                p.St.FrameSeq = s;   // capture keeps counting
                p.Broken = g.EngineBroken(p);
                Assert.False(p.Broken, "broken at " + s);
                Assert.Null(g.EngineStopped(p), "stopped");
                Assert.Null(g.CaptureFrozen(p), "frozen");
                Assert.Null(g.EngineDeaf(p), "deaf");
                Assert.Null(g.CaptureOrphaned(p), "orphaned");
            }
        }

        [Test]
        static void Stopped_capture_rearms_after_brokenPersistSec()
        {
            var g = NewGuard();
            var st = Healthy(); st.CaptureRunning = false;
            for (int s = 0; s < Settings.BrokenPersistSec; s++)
            {
                var p = At(s, st); p.Broken = g.EngineBroken(p);
                Assert.True(p.Broken, "broken");
                Assert.Null(g.EngineStopped(p), "too early at " + s);
            }
            var due = At(Settings.BrokenPersistSec, st); due.Broken = g.EngineBroken(due);
            Assert.Contains("engine stopped (session=Running, capture=False", g.EngineStopped(due));

            due.SinceKick = Settings.RearmGraceSec - 1;
            Assert.Null(g.EngineStopped(due), "not right after a re-arm");
        }

        [Test]
        static void Idle_engine_held_by_the_web_page_is_not_broken()
        {
            var g = NewGuard();
            var st = Healthy(); st.SessionState = "Idle"; st.CaptureRunning = false; st.OutputRunning = false;
            var p = At(0, st); p.WebAttached = true;
            Assert.False(g.EngineBroken(p), "page attached");
            p.WebAttached = false;
            Assert.True(g.EngineBroken(p), "no page");

            var paused = Healthy(); paused.CaptureRunning = false; paused.HidPaused = true;
            Assert.False(g.EngineBroken(At(0, paused)), "HID paused");
            var errored = Healthy(); errored.CaptureError = "0x88890004";
            Assert.True(g.EngineBroken(At(0, errored)), "capture error");
        }

        [Test]
        static void Idle_engine_while_a_native_haptics_game_runs_is_not_broken()
        {
            var root = EngineClient.Obj(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Sandbox.Json(@"{ 'games': [
                { 'gameId': '3263', 'displayName': 'Zenless Zone Zero', 'processNames': ['ZenlessZoneZero'], 'supportedBackends': ['realtime-audio'] },
                { 'gameId': '3309', 'displayName': 'Phantom Blade Zero', 'processNames': ['pbz'], 'supportedBackends': ['native'] }
            ] }")));
            var games = XHapticsGame.ParseList(EngineClient.At(root, "games"));
            var running = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PBZ", "ZenlessZoneZero" };
            Assert.Equal("Phantom Blade Zero", Guard.NativeGame(games, running));
            running.Remove("PBZ");
            Assert.Null(Guard.NativeGame(games, running), "only an X-Haptics game runs");
            Assert.Null(Guard.NativeGame(null, running), "no game list");

            var g = NewGuard();
            var st = Healthy(); st.SessionState = "Idle"; st.CaptureRunning = false; st.OutputRunning = false;
            var p = At(0, st); p.NativeGame = "Phantom Blade Zero";
            Assert.False(g.EngineBroken(p), "native game");
            p.NativeGame = null;
            Assert.True(g.EngineBroken(p), "no native game");
        }

        [Test]
        static void Frozen_capture_needs_audio_and_an_awake_controller()
        {
            var g = NewGuard();
            string reason = null;
            for (int s = 0; s <= Settings.StallSec && reason == null; s++)
                reason = g.CaptureFrozen(At(s, Healthy(), 0.5f));   // FrameSeq stays 1
            Assert.Equal("audio is playing but the engine capture is frozen", reason);

            g = NewGuard();
            for (int s = 0; s <= 2 * Settings.StallSec; s++)
                Assert.Null(g.CaptureFrozen(At(s, Healthy(), 0.5f, false)), "asleep at " + s);
            g = NewGuard();
            for (int s = 0; s <= 2 * Settings.StallSec; s++)
                Assert.Null(g.CaptureFrozen(At(s, Healthy(), 0f)), "silence at " + s);
        }

        [Test]
        static void Deaf_engine_needs_loud_system_audio_on_system_capture()
        {
            var g = NewGuard();
            for (int s = 1; s < Settings.SilentMismatchKick; s++)
                Assert.Null(g.EngineDeaf(At(s, Healthy(), 0.5f)), "poll " + s);
            Assert.Equal("audio is playing but the engine only hears silence", g.EngineDeaf(At(99, Healthy(), 0.5f)));

            g = NewGuard();
            var perApp = Healthy(); perApp.CaptureSource = "process"; perApp.CapturePid = 1234;
            for (int s = 0; s < 3 * Settings.SilentMismatchKick; s++)
                Assert.Null(g.EngineDeaf(At(s, perApp, 0.5f)), "per-app capture at " + s);
        }

        // a poll right after a device change: Windows plays `systemPeak`, the engine hears `enginePeak`
        static Poll Playing(Guard g, int second, float systemPeak, double enginePeak, string source = "system")
        {
            var st = Healthy(); st.FrameSeq = 100 + second; st.EnginePeak = enginePeak; st.CaptureSource = source;
            var p = At(second, st, systemPeak);
            g.CaptureFrozen(p);   // keeps the frame counter bookkeeping that EngineFollowsDevice reads
            return p;
        }

        [Test]
        static void Device_change_is_skipped_only_when_the_engine_hears_the_new_device()
        {
            var g = NewGuard();
            Assert.True(g.EngineFollowsDevice(Playing(g, 0, 0.3f, 0.2)), "engine hears the audio");
            Assert.False(g.EngineFollowsDevice(Playing(g, 1, 0.3f, 0.0)), "engine hears silence (still on the old device)");
            Assert.False(g.EngineFollowsDevice(Playing(g, 2, 0.0f, 0.0)), "nothing plays: cannot tell");
            Assert.False(g.EngineFollowsDevice(Playing(g, 3, 0.3f, 0.2, "process")), "per-app capture: not comparable");

            var stuck = At(10, Playing(g, 4, 0.3f, 0.2).St, 0.3f);   // same frame counter 6 s later
            g.CaptureFrozen(stuck);
            Assert.False(g.EngineFollowsDevice(stuck), "frame counter frozen");
        }

        [Test]
        static void Scheduled_device_change_rearm_is_skipped_when_followed()
        {
            var g = NewGuard();
            var p = Playing(g, 0, 0.3f, 0.2);
            g.Schedule(p.Now, Guard.ReasonDeviceChanged);
            Assert.False(g.RunScheduledKick(p), "no re-arm");
            Assert.Contains("skip re-arm (default output device changed): the engine already follows the new device",
                            System.IO.File.ReadAllText(Log.FilePath));
            Assert.False(g.RunScheduledKick(Playing(g, 1, 0.3f, 0.2)), "nothing scheduled any more");
        }

        [Test]
        static void Master_switch_follows_the_engine()
        {
            bool? candidate = null;
            Assert.Equal((bool?)true, Guard.FollowEngine(true, false, true, false, ref candidate), "first reading after start: follow at once");

            candidate = null;
            Assert.Null(Guard.FollowEngine(false, true, false, false, ref candidate), "a change is not followed on the first sight");
            Assert.Equal((bool?)false, Guard.FollowEngine(false, true, false, false, ref candidate), "confirmed by a second reading");

            candidate = null;
            Assert.Null(Guard.FollowEngine(false, true, false, false, ref candidate), "seen once");
            Assert.Null(Guard.FollowEngine(true, true, false, false, ref candidate), "back to equal: forgotten");
            Assert.Null(Guard.FollowEngine(false, true, false, false, ref candidate), "seen once again, not twice in a row");

            candidate = null;
            Assert.Null(Guard.FollowEngine(false, true, false, true, ref candidate), "during the guard's own re-arm");
            Assert.Null(Guard.FollowEngine(false, true, false, true, ref candidate), "still in grace");
            Assert.Null(Guard.FollowEngine(null, true, true, false, ref candidate), "engine unreachable");
            Assert.Null(Guard.FollowEngine(true, true, true, false, ref candidate), "already equal");
        }

        [Test]
        static void Capture_bound_to_an_exited_process_is_orphaned()
        {
            var g = NewGuard();
            var st = Healthy(); st.CaptureSource = "process"; st.CapturePid = int.MaxValue - 1;   // no such process
            for (int s = 0; s < Settings.BrokenPersistSec; s++)
                Assert.Null(g.CaptureOrphaned(At(s, st)), "too early at " + s);
            Assert.Contains("game exited", g.CaptureOrphaned(At(Settings.BrokenPersistSec, st)));

            g = NewGuard();
            st.CapturePid = System.Diagnostics.Process.GetCurrentProcess().Id;   // alive
            for (int s = 0; s < 3 * Settings.BrokenPersistSec; s++)
                Assert.Null(g.CaptureOrphaned(At(s, st)), "live process at " + s);
        }
    }
}
