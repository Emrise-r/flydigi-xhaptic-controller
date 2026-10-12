using System.Collections.Generic;

namespace FlydigiHapticsGuard
{
    // Typed view of GET /api/v1/xhaptics/configurations: one X-Haptics profile per scope ("global" or a
    // game id), each with an "enabled" flag. The only place that knows that JSON layout.
    class XHapticsConfigs
    {
        readonly Dictionary<string, object> byScope;
        public readonly long Revision;

        XHapticsConfigs(Dictionary<string, object> byScope, long revision)
        {
            this.byScope = byScope ?? new Dictionary<string, object>();
            Revision = revision;
        }

        public static XHapticsConfigs Parse(Dictionary<string, object> root)
        {
            return new XHapticsConfigs(EngineClient.Obj(EngineClient.At(root, "configurations")), EngineClient.Long(root, "revision"));
        }

        public bool IsEmpty { get { return byScope.Count == 0; } }

        public bool Has(string scope) { return Find(scope) != null; }

        public bool IsEnabled(string scope)
        {
            var profile = Find(scope);
            return profile != null && EngineClient.Bool(profile, "enabled");
        }

        // The profile's JSON object, or null when there is no such scope.
        public Dictionary<string, object> Find(string scope)
        {
            object profile;
            return scope != null && byScope.TryGetValue(scope, out profile) ? EngineClient.Obj(profile) : null;
        }

        // Body for PATCH .../configurations/{scope}: the profile's backend and UI settings with a new enabled flag.
        public static Dictionary<string, object> PatchBody(Dictionary<string, object> profile, bool enabled, long expectedRevision)
        {
            var body = new Dictionary<string, object>();
            body["expectedRevision"] = expectedRevision;
            body["enabled"] = enabled;
            body["selectedBackend"] = EngineClient.Str(profile, "selectedBackend") ?? "realtime-audio";
            body["ui"] = EngineClient.At(profile, "ui") ?? new Dictionary<string, object>();
            return body;
        }
    }
}
