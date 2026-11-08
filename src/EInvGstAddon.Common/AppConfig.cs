using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace EInvGstAddon.Common
{
    /// <summary>Add-on configuration. The API secret is never stored in clear text (DPAPI, current Windows user).</summary>
    public sealed class AppConfig
    {
        public string ErpUrl { get; set; } = "https://aits-tenttechcoating.m.frappe.cloud";
        public string ApiKey { get; set; } = "";
        /// <summary>DPAPI-protected, base64. Use <see cref="ApiSecret"/> to read/write.</summary>
        public string ApiSecretProtected { get; set; } = "";
        public string ErpCompany { get; set; } = "Vipra Closures Private Limited";
        public string ErpAbbr { get; set; } = "VCPL";
        public string SapDatabase { get; set; } = "Vipra";
        public int TimeoutSeconds { get; set; } = 60;
        public bool AutoSync { get; set; } = true;
        public int MaxAutoRetries { get; set; } = 5;
        /// <summary>Only invoices with DocEntry >= this value are auto-synchronised (0 = set on first start to "next new invoice").</summary>
        public int SyncStartDocEntry { get; set; } = 0;
        public string SellingPriceList { get; set; } = "Standard Selling";
        /// <summary>Dedicated naming series for this company (the shared site only had the other company's series). Must be an option of Sales Invoice.naming_series.</summary>
        public string SalesInvoiceSeries { get; set; } = "VCPL-SINV-.YYYY.-.#####";
        public string RootWarehouse { get; set; } = "All Warehouses - VCPL";
        public string DefaultCustomerGroup { get; set; } = "Commercial";
        /// <summary>ERPNext requires a valuation method on stock items and this site has no Stock Settings default. Configurable; FIFO or Moving Average.</summary>
        public string DefaultValuationMethod { get; set; } = "FIFO";
        /// <summary>SAP tax type (INV4.staType) -> ERPNext account of the configured company. Never invented: unmapped non-zero taxes stop the sync.</summary>
        public Dictionary<string, string> TaxAccountMap { get; set; } = new Dictionary<string, string>
        {
            { "-100", "Output Tax CGST - VCPL" },
            { "-110", "Output Tax SGST - VCPL" },
            { "-120", "Output Tax IGST - VCPL" }
        };
        public string FreightAccount { get; set; } = "Freight and Forwarding Charges - VCPL";
        /// <summary>Account used to carry the difference when ERPNext cannot hold a SAP unit price at its configured rate precision (e.g. 0.158 -> 0.16).</summary>
        public string RoundOffAccount { get; set; } = "Rounded Off - VCPL";
        /// <summary>ERPNext currency/rate precision (System Settings). Used to predict precision loss of SAP unit prices.</summary>
        public int RatePrecision { get; set; } = 2;
        /// <summary>SAP currency code -> ISO code (the Vipra database stores the local currency under a non-ISO code).</summary>
        public Dictionary<string, string> CurrencyMap { get; set; } = new Dictionary<string, string> { { "`", "INR" }, { "##", "INR" } };

        [JsonIgnore]
        public string ApiSecret
        {
            get
            {
                if (string.IsNullOrEmpty(ApiSecretProtected)) return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ApiSecretProtected), null, DataProtectionScope.CurrentUser)); }
                catch { return ""; }
            }
            set
            {
                ApiSecretProtected = string.IsNullOrEmpty(value) ? "" :
                    Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            }
        }

        [JsonIgnore]
        public string AuthHeader { get { return "token " + ApiKey + ":" + ApiSecret; } }

        /// <summary>Company-level isolation: this add-on may never be pointed at the other company on the shared site.</summary>
        public void ValidateTarget()
        {
            string c = (ErpCompany ?? "").Trim(), a = (ErpAbbr ?? "").Trim();
            if (c.Length == 0 || a.Length == 0) throw new InvalidOperationException("ERPNext company and abbreviation must be configured.");
            if (a.Equals("TTCPL", StringComparison.OrdinalIgnoreCase) || c.IndexOf("Tint Tech", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Safety stop: the target company must not be Tint Tech Coatings (TTCPL).");
        }

        public string MapCurrency(string sapCode)
        {
            string c = (sapCode ?? "").Trim();
            string m;
            return CurrencyMap.TryGetValue(c, out m) ? m : c;
        }
    }

    public static class ConfigStore
    {
        public static string PathOverride;

        public static string FilePath
        {
            get
            {
                return PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EInvGstAddon", "config.json");
            }
        }

        public static AppConfig Load()
        {
            AppConfig cfg = null;
            try { if (File.Exists(FilePath)) cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(FilePath)); }
            catch (Exception ex) { Logger.Warn("config.load.failed", "error", ex.Message); }
            cfg = cfg ?? new AppConfig();
            Logger.RegisterSecret(cfg.ApiSecret);
            Logger.RegisterSecret(cfg.ApiKey);
            return cfg;
        }

        public static void Save(AppConfig cfg)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
            Logger.RegisterSecret(cfg.ApiSecret);
            Logger.RegisterSecret(cfg.ApiKey);
        }
    }
}
