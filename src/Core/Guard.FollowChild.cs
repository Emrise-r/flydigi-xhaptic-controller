using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FlydigiHapticsGuard
{
    partial class Guard
    {
        // ---- follow child audio
        // A game can play audio from a child process (e.g. BH3 -> ZFGameBrowser for web events) that the
        // engine's per-app capture of the game does not hear. While following, the child's own profile is
        // enabled (or Mix when the engine binds a process of the child that plays nothing):
        //   child closed or silent for followReturnSilenceSec -> back to the parent; parent closed -> Mix.

        string followParentScope, followChildScope, followParentName, followChildName;
        int followParentPid, childAudioPolls;
        bool followViaMix;
        DateTime followVerifyAt = DateTime.MaxValue, childSilentSince = DateTime.MaxValue;

        void ClearFollow()
        {
            followParentScope = followChildScope = followParentName = followChildName = null;
            followParentPid = 0; childAudioPolls = 0; followViaMix = false;
            followVerifyAt = childSilentSince = DateTime.MaxValue;
        }

        // Returns true when it switched sources this tick.
        bool FollowChildAudio(DateTime now, XHapticsStatus st)
        {
            if (!Settings.FollowChildAudio || !st.HidConnected) return false;
            if (followParentScope != null) return UpdateFollow(now);
            long capturePid = st.CapturePid;
            string scope = st.Scope;
            if (!st.CaptureRunning || st.SystemCapture || capturePid <= 0 || string.IsNullOrEmpty(scope) || scope == EngineApi.GlobalScope ||
                (now - lastKickAt).TotalSeconds < Settings.RearmGraceSec || st.EnginePeak >= Settings.EngineSilentPeak)
            { childAudioPolls = 0; return false; }

            // the loudest descendant of the captured process
            var parents = ProcessTree.ParentMap();
            var loudest = AudioSessions.SessionPeaks()
                .Where(kv => kv.Key != capturePid && ProcessTree.IsDescendant(kv.Key, (int)capturePid, parents))
                .OrderByDescending(kv => kv.Value)
                .FirstOrDefault();
            if (loudest.Key == 0 || loudest.Value < Settings.AudioPeakThreshold) { childAudioPolls = 0; return false; }
            if (++childAudioPolls < Settings.FollowDetectPolls) return false;
            childAudioPolls = 0;
            StartFollow(scope, (int)capturePid, loudest.Key);
            return true;
        }

        void StartFollow(string parentScope, int parentPid, int childPid)
        {
            followParentScope = parentScope; followParentPid = parentPid;
            followParentName = ProcessName(parentPid) ?? parentScope;
            followChildName = ProcessName(childPid);
            childSilentSince = DateTime.MaxValue;
            Log.Write("child audio: " + followChildName + " (PID " + childPid + ") under " + followParentName + " plays while the engine hears nothing -> following it");

            // the child's own profile (registered as a disabled custom game if needed)
            string childScope = null;
            try
            {
                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { followChildName };
                childScope = MatchGames(LoadGames(false), running, false).Select(g => g.Id).FirstOrDefault();
                string path = ProcessTree.ImagePath(childPid);
                if (childScope == null && !string.IsNullOrEmpty(path)) childScope = RegisterCustomGame(followChildName, path);
            }
            catch (Exception ex) { Log.Write("cannot register " + followChildName + ": " + Trim(ex.Message)); }

            var configs = engine.Configs();
            var originals = SourceOverrides.Load();
            SwitchProfile(configs, originals, childScope, false);
            SwitchProfile(configs, originals, parentScope, false);
            if (configs.Has(childScope))
            {
                Thread.Sleep(BindOnMixDelayMs);
                SwitchProfile(configs, originals, childScope, true);
                followChildScope = childScope; followViaMix = false;
                followVerifyAt = DateTime.Now.AddSeconds(VerifyBindingAfterSec);
            }
            else followViaMix = true;
            SourceOverrides.Save(originals);
            ResetAfterSourceChange();
            LastAction = "Following " + followChildName;
            Notify("Audio from " + followChildName + " (inside " + followParentName + "): following it" + (followViaMix ? " via Mix." : "."), true);
        }

        bool UpdateFollow(DateTime now)
        {
            var parents = ProcessTree.ParentMap();
            if (!ProcessAlive(followParentPid)) { EndFollow(followParentName + " closed", true); return true; }

            // engine bound to a process of the child that plays nothing (multi-process child) -> Mix
            if (!followViaMix && now >= followVerifyAt)
            {
                followVerifyAt = DateTime.MaxValue;
                var st = engine.Status();
                long bound = st.CapturePid;
                bool ok = st.ProcessCapture && bound > 0 &&
                          ProcessTree.TreeHasSession((int)bound, AudioSessions.ProcessIds(), parents);
                if (!ok)
                {
                    Log.Write("engine bound PID " + bound + " of " + followChildName + ", which plays nothing -> Mix");
                    var originals = SourceOverrides.Load();
                    SwitchProfile(engine.Configs(), originals, followChildScope, false);
                    SourceOverrides.Save(originals);
                    followViaMix = true; lastKickAt = DateTime.Now;
                    Notify(followChildName + ": switched to Mix (the engine cannot capture it on its own).", true);
                    return true;
                }
            }

            // is the child (any process of that name under the parent) still alive and playing?
            var childPids = new List<int>();
            ProcessTree.ForEach(Process.GetProcessesByName(followChildName), p =>
            {
                if (ProcessTree.IsDescendant(p.Id, followParentPid, parents)) childPids.Add(p.Id);
            });
            if (childPids.Count == 0) { EndFollow(followChildName + " closed", false); return true; }
            var peaks = AudioSessions.SessionPeaks();
            float childPeak = childPids.Max(pid => { float peak; return peaks.TryGetValue(pid, out peak) ? peak : 0f; });
            if (childPeak >= Settings.AudioPeakThreshold) childSilentSince = DateTime.MaxValue;
            else if (childSilentSince == DateTime.MaxValue) childSilentSince = now;
            if ((now - childSilentSince).TotalSeconds >= Settings.FollowReturnSilenceSec)
            { EndFollow(followChildName + " silent for " + Settings.FollowReturnSilenceSec + "s", false); return true; }
            return false;
        }

        void EndFollow(string reason, bool toMix)
        {
            string parentName = followParentName, parentScope = followParentScope, childScope = followChildScope;
            ClearFollow();
            try
            {
                var configs = engine.Configs(); var originals = SourceOverrides.Load();
                SwitchProfile(configs, originals, childScope, false);
                if (!toMix)
                {
                    Thread.Sleep(BindOnMixDelayMs);
                    SwitchProfile(configs, originals, parentScope, true);
                }
                SourceOverrides.Save(originals);
            }
            catch (Exception ex) { Log.Write("end follow failed: " + Trim(ex.Message)); }
            ResetAfterSourceChange();
            string target = toMix ? "Mix (all apps)" : parentName;
            Log.Write("stop following (" + reason + ") -> " + target);
            LastAction = "Source: " + target;
            Notify(reason + ": audio source back to " + target + ".", true);
        }
    }
}
