using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace FlydigiHapticsGuard
{
    // Status = HTTP status code, 0 when the engine could not be reached.
    class EngineException : Exception
    {
        public int Status;
        public EngineException(int status, string msg) : base(msg) { Status = status; }
    }

    // HTTP client for the engine's local API, authenticated like the Space Station web page.
    class EngineClient
    {
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string token;

        string Send(string method, string path, string body, bool auth)
        {
            var req = (HttpWebRequest)WebRequest.Create(Settings.EngineBase + path);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 4000;
            req.ReadWriteTimeout = 4000;
            req.Headers["Origin"] = Settings.WebOrigin;
            req.Headers["X-Client-Name"] = EngineApi.ClientName;
            req.Headers["X-Protocol-Version"] = EngineApi.ProtocolVersion;
            if (auth) req.Headers["Authorization"] = "Bearer " + token;
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new EngineException(0, ex.Message);
                string text;
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
                throw new EngineException((int)resp.StatusCode, text);
            }
        }

        void Bootstrap()
        {
            var v = Obj(json.DeserializeObject(Send("GET", EngineApi.Version, null, false)));
            token = Str(v, "accessToken");
            if (string.IsNullOrEmpty(token)) throw new EngineException(0, "bootstrap returned no access token");
            Version = Str(v, "version") ?? "unknown";
            var caps = At(v, "capabilities") as object[] ?? new object[0];
            capabilities = new HashSet<string>(caps.Select(c => Convert.ToString(c)));
        }

        HashSet<string> capabilities = new HashSet<string>();

        // engine version reported at the last bootstrap ("5.0.2.4"), null before the first call
        public string Version { get; private set; }

        // engine 5.0.2.4+: the global X-Haptics profile is one "single config" saved with PUT; the old PATCH is gone (410)
        bool SingleGlobalConfig { get { return capabilities.Contains(EngineApi.SingleConfigCapability); } }

        string Call(string method, string path, string body)
        {
            if (token == null) Bootstrap();
            try { return Send(method, path, body, true); }
            catch (EngineException ex)
            {
                if (ex.Status != 401) throw;
                Bootstrap();
                return Send(method, path, body, true);
            }
        }

        public Dictionary<string, object> Get(string path)
        {
            return Obj(json.DeserializeObject(Call("GET", path, null)));
        }

        public Dictionary<string, object> Patch(string path, object body)
        {
            return Obj(json.DeserializeObject(Call("PATCH", path, json.Serialize(body))));
        }

        public Dictionary<string, object> Post(string path, object body)
        {
            return Obj(json.DeserializeObject(Call("POST", path, json.Serialize(body))));
        }

        public Dictionary<string, object> Put(string path, object body)
        {
            return Obj(json.DeserializeObject(Call("PUT", path, json.Serialize(body))));
        }

        // ---- typed calls

        public XHapticsStatus Status()
        {
            return XHapticsStatus.Parse(Get(EngineApi.Status));
        }

        public XHapticsConfigs Configs()
        {
            return XHapticsConfigs.Parse(Get(EngineApi.Configurations));
        }

        // Only for user-triggered calls: the engine rescans its games and drops to Mix for ~2 s.
        public List<XHapticsGame> Games()
        {
            return XHapticsGame.ParseList(At(Get(EngineApi.Games), "games"));
        }

        // Adds an app to the engine's game list; returns its game id.
        public string AddCustomGame(string displayName, string processPath)
        {
            var body = new Dictionary<string, object>();
            body["displayName"] = displayName;
            body["processPath"] = processPath;
            return Str(Post(EngineApi.CustomGames, body), "game.gameId");
        }

        // Sets one profile's enabled flag; retried once when another client changed the revision (409).
        public void SetEnabled(XHapticsConfigs configs, string scope, bool enabled)
        {
            if (scope == EngineApi.GlobalScope && SingleGlobalConfig) { SetGlobalEnabled(enabled); return; }
            var profile = configs.Find(scope);
            if (profile == null) return;
            RetryOnConflict(delegate
            {
                long revision = Configs().Revision;
                Patch(EngineApi.Configuration(scope), XHapticsConfigs.PatchBody(profile, enabled, revision));
            });
        }

        // Engine 5.0.2.4+: read the complete global config and save it back with the new enabled flag.
        void SetGlobalEnabled(bool enabled)
        {
            string path = EngineApi.RealtimeAudioConfiguration(EngineApi.GlobalScope);
            RetryOnConflict(delegate
            {
                var snapshot = Get(path);
                var config = Obj(At(snapshot, "config"));
                if (config == null) throw new EngineException(0, "the engine returned no global configuration");
                config["enabled"] = enabled;
                var body = new Dictionary<string, object>();
                body["expectedRevision"] = Long(snapshot, "revision");
                body["config"] = config;
                Put(path, body);
            });
        }

        static void RetryOnConflict(Action write)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { write(); return; }
                catch (EngineException ex) { if (ex.Status != 409 || attempt == 1) throw; }
            }
        }

        // ---- tiny JSON helpers
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static object At(Dictionary<string, object> o, string path)
        {
            object cur = o;
            foreach (var key in path.Split('.'))
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out cur)) return null;
            }
            return cur;
        }
        public static string Str(Dictionary<string, object> o, string path) { var v = At(o, path); return v == null ? null : Convert.ToString(v); }
        public static bool Bool(Dictionary<string, object> o, string path) { var v = At(o, path); return v is bool && (bool)v; }
        public static long Long(Dictionary<string, object> o, string path) { var v = At(o, path); try { return v == null ? -1 : Convert.ToInt64(v); } catch { return -1; } }
        public static double Num(Dictionary<string, object> o, string path) { var v = At(o, path); try { return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; } }
    }
}
