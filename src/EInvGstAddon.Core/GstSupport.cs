using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.Core
{
    /// <summary>One line of a validation / check report shown to the SAP user.</summary>
    public sealed class CheckLine
    {
        public string Name, Message; public bool Ok, Warning;
    }

    /// <summary>Decodes (without verifying) the payload of a signed IRP document; used only to DISPLAY what the IRP signed.</summary>
    public static class Jwt
    {
        public static JObject DecodePayload(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return null;
                var parts = token.Split('.');
                if (parts.Length < 2) return null;
                string p = parts[1].Replace('-', '+').Replace('_', '/');
                p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
                var payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
                var data = payload["data"];
                if (data != null && data.Type == JTokenType.String) { try { return JObject.Parse((string)data); } catch { } }
                return payload;
            }
            catch { return null; }
        }
    }

    /// <summary>GST state names by code (for the vehicle-update dialog; the server needs the code).</summary>
    public static class GstStateNames
    {
        public static readonly SortedDictionary<string, string> ByCode = new SortedDictionary<string, string>
        {
            { "01", "Jammu and Kashmir" }, { "02", "Himachal Pradesh" }, { "03", "Punjab" }, { "04", "Chandigarh" }, { "05", "Uttarakhand" }, { "06", "Haryana" }, { "07", "Delhi" }, { "08", "Rajasthan" },
            { "09", "Uttar Pradesh" }, { "10", "Bihar" }, { "11", "Sikkim" }, { "12", "Arunachal Pradesh" }, { "13", "Nagaland" }, { "14", "Manipur" }, { "15", "Mizoram" }, { "16", "Tripura" },
            { "17", "Meghalaya" }, { "18", "Assam" }, { "19", "West Bengal" }, { "20", "Jharkhand" }, { "21", "Odisha" }, { "22", "Chhattisgarh" }, { "23", "Madhya Pradesh" }, { "24", "Gujarat" },
            { "26", "Dadra and Nagar Haveli and Daman and Diu" }, { "27", "Maharashtra" }, { "29", "Karnataka" }, { "30", "Goa" }, { "31", "Lakshadweep" }, { "32", "Kerala" }, { "33", "Tamil Nadu" },
            { "34", "Puducherry" }, { "35", "Andaman and Nicobar Islands" }, { "36", "Telangana" }, { "37", "Andhra Pradesh" }, { "38", "Ladakh" }
        };

        public static string[] Options()
        {
            var l = new List<string>();
            foreach (var kv in ByCode) l.Add(kv.Key + " - " + kv.Value);
            return l.ToArray();
        }
    }
}
