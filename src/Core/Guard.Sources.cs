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
    partial class Guard
    {
        public void RequestCycle() { cycleRequested = true; }
        volatile bool cycleRequested;
        DateTime? sourceCheckAt; string sourceCheckLabel;

        // Multi-process apps (e.g. Chrome plays audio from a separate audio-service process): the engine captures
        // the process tree of the app's foreground-window process while that window is focused, and otherwise a
        // process it picked by name (often one that plays nothing). The guard cannot change that; it only says so.
        void CheckSourceProcess()
        {
            var st = engine.Get("/api/xhaptics/status");
            if (EngineClient.Str(st, "realtimeAudio.wasapi.source") != "process") return;
            long pid = EngineClient.Long(st, "realtimeAudio.wasapi.processId");
            if (pid <= 0) return;
            var sessions = AudioSessions.ProcessIds();
            var parents = ProcessTree.ParentMap();
            if (ProcessTree.TreeHasSession((int)pid, sessions, parents)) return;
            // only complain when another process of the same app is the one playing
            string name = ProcessName((int)pid);
            bool appPlays = false;
            foreach (int s in sessions) if (string.Equals(ProcessName(s), name, StringComparison.OrdinalIgnoreCase)) appPlays = true;
            if (!appPlays) return;
            Log.Write("audio source " + sourceCheckLabel + ": engine captures PID " + pid + ", which plays no audio (multi-process app; only captured while its window is focused)");
            Notify(sourceCheckLabel + " plays audio from another process. The engine only captures it while the " + sourceCheckLabel +
                   " window is focused. Use Mix to feel it in the background.", true);
        }

        static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return null; }
        }
        DateTime nextRestoreCheck = DateTime.MinValue;

        // ---- audio source cycling
        // The engine captures one process (process-loopback) while an enabled game/app scope is running,
        // and the whole system mix when none is. Cycling flips the enabled flags of the running scopes:
        //   Mix -> app/game 1 -> app/game 2 -> ... -> Mix
        // The original flags are kept in sources.txt and put back once those processes have exited.

        class GameInfo { public string Id, Name, Process; }

        object[] cachedGames; DateTime cachedGamesStamp;
        readonly object gamesGate = new object();

        // GET /api/v1/xhaptics/games makes the engine rescan games and drop to Mix for ~2 s, so the list is
        // read from the engine's own games file instead; the API is only a fallback for user-triggered calls.
        object[] LoadGames(bool allowApi)
        {
            lock (gamesGate) return LoadGamesLocked(allowApi);
        }

        object[] LoadGamesLocked(bool allowApi)
        {
            try
            {
                var fi = new FileInfo(Settings.GameListPath);
                if (fi.Exists)
                {
                    if (cachedGames == null || fi.LastWriteTimeUtc != cachedGamesStamp)
                    {
                        string text;
                        using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
                        var root = EngineClient.Obj(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text));
                        cachedGames = EngineClient.At(root, "games") as object[];
                        cachedGamesStamp = fi.LastWriteTimeUtc;
                    }
                    if (cachedGames != null) return cachedGames;
                }
            }
            catch (Exception ex) { Log.Write("cannot read games file: " + Trim(ex.Message)); }
            return allowApi ? EngineClient.At(engine.Get("/api/v1/xhaptics/games"), "games") as object[] : null;
        }

        // Registered games/apps whose process is running. applyExclude drops the ones listed in
        // config.json "sources.exclude" (they are never picked as a target, but are still switched off).
        List<GameInfo> RunningGames(bool allowApi, bool applyExclude)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) { try { running.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
            return MatchGames(LoadGames(allowApi), running, applyExclude);
        }

        static List<GameInfo> MatchGames(object[] games, HashSet<string> processNames, bool applyExclude)
        {
            var result = new List<GameInfo>();
            if (games == null) return result;
            foreach (var o in games)
            {
                var g = EngineClient.Obj(o);
                if (g == null) continue;
                var names = EngineClient.At(g, "processNames") as object[];
                if (names == null) continue;
                foreach (var n in names)
                {
                    string name = Settings.NormalizeProcessName(Convert.ToString(n));
                    if (name.Length > 0 && processNames.Contains(name))
                    {
                        if (!(applyExclude && Settings.IsExcluded(name)))
                            result.Add(new GameInfo { Id = EngineClient.Str(g, "gameId"), Name = EngineClient.Str(g, "displayName") ?? name, Process = name });
                        break;
                    }
                }
            }
            result.Sort(delegate(GameInfo a, GameInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return result;
        }

        // ---- "Audio sources" tray menu

        public class SourceEntry
        {
            public string Process, Path, Label, GameId;   // GameId == null: not registered in X-Haptics yet
            public bool Included;                         // registered and not excluded = part of the cycle
            public string ParentLabel;                    // set when this runs inside another listed app (BH3 -> ZFGameBrowser)
        }

        static Dictionary<string, List<int>> PidsByName()
        {
            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { List<int> l; if (!map.TryGetValue(p.ProcessName, out l)) map[p.ProcessName] = l = new List<int>(); l.Add(p.Id); }
                catch { }
                finally { p.Dispose(); }
            }
            return map;
        }

        // Which of the candidate process names runs `name` as a descendant (e.g. BH3 for ZFGameBrowser)?
        static string ParentProcessOf(string name, IEnumerable<string> candidates, Dictionary<string, List<int>> pids, Dictionary<int, int> parents)
        {
            List<int> mine;
            if (!pids.TryGetValue(name, out mine)) return null;
            foreach (var cand in candidates)
            {
                List<int> theirs;
                if (string.Equals(cand, name, StringComparison.OrdinalIgnoreCase) || !pids.TryGetValue(cand, out theirs)) continue;
                foreach (int c in mine) foreach (int a in theirs) if (ProcessTree.IsDescendant(c, a, parents)) return cand;
            }
            return null;
        }

        // Called on the UI thread when the menu opens: processes with an audio session plus running
        // registered games. Reads only the engine's games file (never the games API).
        public List<SourceEntry> SourceEntries()
        {
            var byName = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (int pid in AudioSessions.ProcessIds())
            {
                if (pid == self) continue;
                string path = ProcessTree.ImagePath(pid);
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(path, Settings.EngineExePath, StringComparison.OrdinalIgnoreCase)) continue;   // the engine itself
                string name = Settings.NormalizeProcessName(System.IO.Path.GetFileName(path));
                if (byName.ContainsKey(name)) continue;
                string label = name;
                try { var d = FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrEmpty(d) && d.Trim().Length > 0) label = d.Trim(); } catch { }
                byName[name] = new SourceEntry { Process = name, Path = path, Label = label };
            }

            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) { try { running.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
            foreach (var g in MatchGames(LoadGames(false), running, false))
            {
                SourceEntry e;
                if (!byName.TryGetValue(g.Process, out e)) byName[g.Process] = e = new SourceEntry { Process = g.Process };
                e.GameId = g.Id; e.Label = g.Name;
            }

            var list = new List<SourceEntry>(byName.Values);
            foreach (var e in list) e.Included = e.GameId != null && !Settings.IsExcluded(e.Process);

            // nest child processes under the listed app that runs them
            var pids = PidsByName(); var parents = ProcessTree.ParentMap();
            var names = new List<string>(byName.Keys);
            foreach (var e in list)
            {
                string parent = ParentProcessOf(e.Process, names, pids, parents);
                if (parent != null) e.ParentLabel = byName[parent].Label;
            }
            list.Sort(delegate(SourceEntry a, SourceEntry b)
            {
                string ka = (a.ParentLabel ?? a.Label) + "\u0001" + (a.ParentLabel == null ? "" : a.Label);
                string kb = (b.ParentLabel ?? b.Label) + "\u0001" + (b.ParentLabel == null ? "" : b.Label);
                return string.Compare(ka, kb, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }


        // Include: register the process as a disabled custom game if needed and drop it from the exclude list.
        // Exclude: add it to the exclude list (nothing is removed from Flydigi's game list).
        public void RequestSourceInclude(SourceEntry e, bool include)
        {
            Enqueue(delegate
            {
                try
                {
                    var exclude = new List<string>(Settings.SourceExclude);
                    exclude.RemoveAll(delegate(string x) { return string.Equals(x, e.Process, StringComparison.OrdinalIgnoreCase); });
                    if (include)
                    {
                        if (e.GameId == null)
                        {
                            if (string.IsNullOrEmpty(e.Path)) throw new InvalidOperationException("unknown path for " + e.Process);
                            var body = new Dictionary<string, object>();
                            body["displayName"] = e.Label;
                            body["processPath"] = e.Path;
                            var r = engine.Post("/api/v1/xhaptics/games/custom", body);
                            string id = EngineClient.Str(r, "game.gameId");
                            // new custom games start disabled; make sure, so the engine keeps capturing Mix
                            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
                            var c = id != null && configs != null && configs.ContainsKey(id) ? EngineClient.Obj(configs[id]) : null;
                            if (c != null && EngineClient.Bool(c, "enabled")) Toggle(id, c, false);
                            lastKickAt = DateTime.Now;
                            Log.Write("registered " + e.Path + " as custom game " + id + " (disabled)");
                        }
                    }
                    else exclude.Add(e.Process);
                    Settings.SaveSourceExclude(exclude);
                    Log.Write("audio source " + e.Process + (include ? " included" : " excluded"));
                    Notify(e.Label + (include ? " added to" : " removed from") + " the audio source cycle.", true);
                }
                catch (Exception ex)
                {
                    Log.Write("audio source " + (include ? "include" : "exclude") + " failed (" + e.Process + "): " + Trim(ex.Message));
                    Notify("Could not update audio sources. " + Explain(ex), true);
                }
            });
        }

        void Cycle()
        {
            if (!autoEnabled)
            {
                Log.Write("audio source switch ignored: switch is OFF");
                Notify("X-Haptics is off. Turn it on first" + (Settings.HotkeyToggle.Length > 0 ? " (" + Settings.HotkeyToggle + ")." : "."), true);
                return;
            }

            if (followParentScope != null) { Log.Write("manual source switch: stop following " + followChildName); ClearFollow(); }

            var games = RunningGames(true, false);   // all running registered scopes (all get switched off)
            if (games.Count == 0)
            {
                Notify("Audio source: Mix (all apps). No registered game or app is running.", true);
                return;
            }
            var targets = games.FindAll(delegate(GameInfo g) { return !Settings.IsExcluded(g.Process); });
            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));

            // Current source = the enabled running scope (the engine's own status is unreliable while it is
            // switching or paused by the Space Station page). If several are enabled, ask the engine.
            var enabledIds = new List<string>();
            foreach (var g in games)
                if (configs != null && configs.ContainsKey(g.Id) && EngineClient.Bool(EngineClient.Obj(configs[g.Id]), "enabled")) enabledIds.Add(g.Id);
            string current = "mix";
            if (enabledIds.Count == 1) current = enabledIds[0];
            else if (enabledIds.Count > 1)
            {
                string scope = EngineClient.Str(engine.Get("/api/xhaptics/status"), "session.scope");
                current = enabledIds.Contains(scope) ? scope : enabledIds[0];
            }

            // an excluded scope that is currently selected (idx -1) moves on to Mix
            var order = new List<string> { "mix" };
            foreach (var g in targets) order.Add(g.Id);
            int idx = order.IndexOf(current);
            string next = order[(idx + 1) % order.Count];
            var originals = SourceOverrides.Load();
            // The engine only picks up a game scope when it is enabled while the engine sits on global,
            // so: disable every running scope (target included) -> engine falls back to Mix -> enable target.
            foreach (var g in games)
            {
                var c = configs != null && configs.ContainsKey(g.Id) ? EngineClient.Obj(configs[g.Id]) : null;
                if (c == null) continue;
                bool have = EngineClient.Bool(c, "enabled");
                if (!originals.ContainsKey(g.Id)) originals[g.Id] = have;
                if (have) Toggle(g.Id, c, false);
            }
            SourceOverrides.Save(originals);
            if (next != "mix" && configs != null && configs.ContainsKey(next))
            {
                Thread.Sleep(1200);
                Toggle(next, EngineClient.Obj(configs[next]), true);
            }
            lastKickAt = DateTime.Now; brokenSince = DateTime.MaxValue; deadPidSince = DateTime.MaxValue; silentMismatch = 0;

            string label = "Mix (all apps)";
            if (next != "mix")
            {
                var target = games.Find(delegate(GameInfo g) { return g.Id == next; });
                var names = new List<string>(); foreach (var g in games) names.Add(g.Process);
                string parent = ParentProcessOf(target.Process, names, PidsByName(), ProcessTree.ParentMap());
                var parentGame = parent == null ? null : games.Find(delegate(GameInfo g) { return string.Equals(g.Process, parent, StringComparison.OrdinalIgnoreCase); });
                label = target.Name + (parentGame != null ? " (in " + parentGame.Name + ")" : "");
            }
            Log.Write("audio source -> " + label + " (" + next + ")");
            LastAction = "Source: " + label;
            Notify("Audio source: " + label, true);
            if (next != "mix") { sourceCheckAt = DateTime.Now.AddSeconds(4); sourceCheckLabel = label; }
        }

        // Put back the enabled flags changed by Cycle() once none of those processes is running.
        void RestoreSourcesIfDone()
        {
            if (!autoEnabled || DateTime.Now < nextRestoreCheck) return;
            nextRestoreCheck = DateTime.Now.AddSeconds(5);
            var originals = SourceOverrides.Load();
            if (originals.Count == 0) return;
            var running = new HashSet<string>();
            var games = RunningGames(false, false);
            if (games.Count == 0 && cachedGames == null) return;   // games file unreadable: don't guess
            foreach (var g in games) running.Add(g.Id);
            foreach (var id in originals.Keys) if (running.Contains(id)) return;

            var configs = EngineClient.Obj(EngineClient.At(engine.Get("/api/v1/xhaptics/configurations"), "configurations"));
            foreach (var kv in originals)
            {
                var c = configs != null && configs.ContainsKey(kv.Key) ? EngineClient.Obj(configs[kv.Key]) : null;
                if (c != null && EngineClient.Bool(c, "enabled") != kv.Value) Toggle(kv.Key, c, kv.Value);
            }
            SourceOverrides.Save(new Dictionary<string, bool>());
            Log.Write("audio source overrides restored (" + string.Join(",", originals.Keys) + ")");
        }
    }
}
