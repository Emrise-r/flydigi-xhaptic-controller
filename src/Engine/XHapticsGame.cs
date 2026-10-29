using System;
using System.Collections.Generic;
using System.Linq;

namespace FlydigiHapticsGuard
{
    // One entry of the engine's game list (its games file, or GET /api/v1/xhaptics/games).
    class XHapticsGame
    {
        public string Id, Name;
        public readonly List<string> ProcessNames = new List<string>();   // without ".exe"
        public readonly List<string> Backends = new List<string>();       // "supportedBackends"

        // only native haptics: the game drives the controller itself and X-Haptics stays idle
        public bool NativeOnly
        {
            get { return Backends.Count > 0 && Backends.All(b => b == EngineApi.NativeBackend); }
        }

        // The "games" array of the list, or null when it is missing.
        public static List<XHapticsGame> ParseList(object games)
        {
            var array = games as object[];
            if (array == null) return null;
            var list = new List<XHapticsGame>();
            foreach (var o in array)
            {
                var g = EngineClient.Obj(o);
                if (g == null) continue;
                var game = new XHapticsGame { Id = EngineClient.Str(g, "gameId"), Name = EngineClient.Str(g, "displayName") };
                var names = EngineClient.At(g, "processNames") as object[];
                if (names != null)
                    foreach (var n in names)
                    {
                        string name = Settings.NormalizeProcessName(Convert.ToString(n));
                        if (name.Length > 0) game.ProcessNames.Add(name);
                    }
                var backends = EngineClient.At(g, "supportedBackends") as object[];
                if (backends != null)
                    game.Backends.AddRange(backends.Select(b => Convert.ToString(b)).Where(b => !string.IsNullOrEmpty(b)));
                list.Add(game);
            }
            return list;
        }
    }
}
