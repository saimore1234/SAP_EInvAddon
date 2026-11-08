using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    /// <summary>
    /// Direct Frappe/ERPNext REST client (Authorization: token key:secret over HTTPS).
    /// Safety: only reads, inserts and updates are exposed (there is no delete), and any write that names a company
    /// other than the configured one is refused before it leaves the machine.
    /// </summary>
    public class ErpClient : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly HttpClient _http;

        public ErpClient(AppConfig cfg, HttpMessageHandler handler = null)
        {
            _cfg = cfg;
            _http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            System.Net.ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, cfg.TimeoutSeconds));
        }

        public AppConfig Config { get { return _cfg; } }

        private Uri Url(string path, IDictionary<string, string> query)
        {
            string baseUrl = (_cfg.ErpUrl ?? "").TrimEnd('/');
            if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("ERPNext URL must use HTTPS.");
            var sb = new StringBuilder(baseUrl).Append(path);
            if (query != null && query.Count > 0)
                sb.Append('?').Append(string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));
            return new Uri(sb.ToString());
        }

        private static string Enc(string s) { return Uri.EscapeDataString(s); }

        private JToken Send(HttpMethod method, string path, IDictionary<string, string> query, JObject body)
        {
            if (string.IsNullOrEmpty(_cfg.ApiKey) || string.IsNullOrEmpty(_cfg.ApiSecret))
                throw new ErpAuthException("ERPNext API key/secret are not configured.");
            if (method != HttpMethod.Get) Guard(path, body);

            var req = new HttpRequestMessage(method, Url(path, query));
            req.Headers.TryAddWithoutValidation("Authorization", _cfg.AuthHeader);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.ExpectContinue = false;     // some proxies drop the connection on Expect: 100-continue
            req.Headers.ConnectionClose = true;     // never reuse a pooled connection the proxy may have closed (stale keep-alive)
            if (body != null) req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            string text;
            try
            {
                resp = _http.SendAsync(req).GetAwaiter().GetResult();
                text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (TaskCanceledException ex) { throw new ErpUnavailableException("ERPNext request timed out.", ex); }
            catch (HttpRequestException ex) { throw new ErpUnavailableException("ERPNext is not reachable: " + ex.Message, ex); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is WebException) { throw new ErpUnavailableException("Network failure: " + ex.Message, ex); }

            int code = (int)resp.StatusCode;
            Logger.Debug("erp.http", "method", method.Method, "path", path, "status", code);
            if (code >= 200 && code < 300)
            {
                try { return string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text); }
                catch (JsonException) { throw new ErpUnavailableException("ERPNext returned a non-JSON response (HTTP " + code + ")."); }
            }
            if (code == 401 || code == 403)
                throw new ErpAuthException("ERPNext rejected the API credentials/permissions (HTTP " + code + "): " + ExtractMessage(text));
            if (code == 429 || code >= 500)
                throw new ErpUnavailableException("ERPNext unavailable (HTTP " + code + "): " + ExtractMessage(text));
            throw new ErpApiException(ExtractMessage(text), code, ExcType(text));
        }

        /// <summary>Hard safety rules for the production site shared with another company.</summary>
        private void Guard(string path, JObject body)
        {
            try { _cfg.ValidateTarget(); } catch (InvalidOperationException ex) { throw new ConfigurationException(ex.Message); }
            string company = body == null ? null : (string)body["company"];
            if (!string.IsNullOrEmpty(company) && !string.Equals(company, _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Safety stop: refusing to write a document for company '" + company + "'. Only '" + _cfg.ErpCompany + "' may be written.");
            string abbr = _cfg.ErpAbbr ?? "";
            if (path.IndexOf("TTCPL", StringComparison.OrdinalIgnoreCase) >= 0 && !string.Equals(abbr, "TTCPL", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Safety stop: refusing to modify a TTCPL record (" + path + ").");
            if (body != null && body.ToString(Formatting.None).IndexOf("TTCPL", StringComparison.OrdinalIgnoreCase) >= 0 && !string.Equals(abbr, "TTCPL", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Safety stop: refusing to write a payload that references TTCPL.");
        }

        private static string ExcType(string text)
        {
            try { return (string)JObject.Parse(text)["exc_type"]; } catch { return null; }
        }

        public static string ExtractMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(empty response)";
            try
            {
                var o = JObject.Parse(text);
                var sm = (string)o["_server_messages"];
                if (!string.IsNullOrEmpty(sm))
                {
                    var parts = new List<string>();
                    foreach (var m in JArray.Parse(sm))
                    {
                        try { parts.Add(StripHtml((string)JObject.Parse((string)m)["message"])); } catch { parts.Add(StripHtml(m.ToString())); }
                    }
                    if (parts.Count > 0) return string.Join(" | ", parts);
                }
                string exc = (string)o["exception"];
                if (!string.IsNullOrEmpty(exc)) return StripHtml(exc);
                string msg = (string)o["message"];
                if (!string.IsNullOrEmpty(msg)) return StripHtml(msg);
            }
            catch { }
            return StripHtml(text.Length > 400 ? text.Substring(0, 400) : text);
        }

        private static string StripHtml(string s) { return System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", " ").Replace("&nbsp;", " ").Trim(); }

        // ---------- high-level operations ----------
        public JObject Method(string dottedPath, IDictionary<string, string> query = null)
        {
            return Send(HttpMethod.Get, "/api/method/" + dottedPath, query, null) as JObject;
        }

        public JArray GetList(string doctype, string[] fields, object filters = null, int limit = 500, string orderBy = null)
        {
            var q = new Dictionary<string, string>
            {
                { "fields", JsonConvert.SerializeObject(fields) },
                { "limit_page_length", limit.ToString() }
            };
            if (filters != null) q["filters"] = JsonConvert.SerializeObject(filters);
            if (orderBy != null) q["order_by"] = orderBy;
            var res = Send(HttpMethod.Get, "/api/resource/" + Enc(doctype), q, null);
            return (JArray)res["data"];
        }

        /// <summary>Returns null if the document does not exist.</summary>
        public JObject GetDoc(string doctype, string name)
        {
            try
            {
                var res = Send(HttpMethod.Get, "/api/resource/" + Enc(doctype) + "/" + Enc(name), null, null);
                return (JObject)res["data"];
            }
            catch (ErpApiException ex) when (ex.StatusCode == 404) { return null; }
        }

        public JObject Insert(string doctype, JObject doc)
        {
            var res = Send(HttpMethod.Post, "/api/resource/" + Enc(doctype), null, doc);
            return (JObject)res["data"];
        }

        public JObject Update(string doctype, string name, JObject fields)
        {
            var res = Send(HttpMethod.Put, "/api/resource/" + Enc(doctype) + "/" + Enc(name), null, fields);
            return (JObject)res["data"];
        }

        public bool Exists(string doctype, string name) { return GetDoc(doctype, name) != null; }

        public void Dispose() { _http.Dispose(); }
    }
}
