using System;
using System.Collections.Generic;
using System.IO;
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
        }

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

        // PATCH one profile's enabled flag; retried once when another client changed the revision (409).
        public void SetEnabled(XHapticsConfigs configs, string scope, bool enabled)
        {
            var profile = configs.Find(scope);
            if (profile == null) return;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                long revision = Configs().Revision;
                try { Patch(EngineApi.Configuration(scope), XHapticsConfigs.PatchBody(profile, enabled, revision)); return; }
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
