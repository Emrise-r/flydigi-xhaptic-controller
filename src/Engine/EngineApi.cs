using System;

namespace FlydigiHapticsGuard
{
    // Endpoints and fixed values of the xHaptics Engine's local API (undocumented; taken from the Space
    // Station web page).
    static class EngineApi
    {
        public const string Version = "/api/engine/version";               // bootstrap: returns the access token
        public const string Status = "/api/xhaptics/status";
        public const string Configurations = "/api/v1/xhaptics/configurations";
        public const string Games = "/api/v1/xhaptics/games";              // makes the engine rescan (drops to Mix ~2 s)
        public const string CustomGames = "/api/v1/xhaptics/games/custom";

        public static string Configuration(string scope) { return Configurations + "/" + Uri.EscapeDataString(scope); }

        // engine 5.0.2.4+: the global profile as one complete config (GET, then PUT {expectedRevision, config})
        public const string RealtimeAudioConfigurations = "/api/v1/xhaptics/realtime-audio/configurations";
        public static string RealtimeAudioConfiguration(string scope) { return RealtimeAudioConfigurations + "/" + Uri.EscapeDataString(scope); }
        public const string SingleConfigCapability = "realtime-audio-single-config-v1";

        // headers the web page sends; the engine checks them
        public const string ClientName = "space-station-web";
        public const string ProtocolVersion = "1";

        // the profile used when no game/app profile is running (captures the whole mix)
        public const string GlobalScope = "global";
    }
}
