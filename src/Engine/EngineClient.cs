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
    // ---------------------------------------------------------------- Engine HTTP client
    class EngineException : Exception
    {
        public int Status;
        public EngineException(int status, string msg) : base(msg) { Status = status; }
    }

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
            req.Headers["X-Client-Name"] = "space-station-web";
            req.Headers["X-Protocol-Version"] = "1";
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
            var v = Obj(json.DeserializeObject(Send("GET", "/api/engine/version", null, false)));
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
