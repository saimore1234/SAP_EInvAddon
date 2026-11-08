using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace EInvGstAddon.Common
{
    /// <summary>
    /// One integration configuration = SAP company database + ERPNext server + ERPNext company (+ stable Id).
    /// Nothing customer-specific is hardcoded: every value is entered or discovered through the Settings screen.
    /// The API secret is never stored in clear text (DPAPI, current Windows user).
    /// </summary>
    public sealed class AppConfig
    {
        // ----- identity -----
        /// <summary>Stable id (short random hex) that scopes every mapping, queue entry, log and audit record.</summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Active { get; set; } = true;
        /// <summary>Production or Sandbox. Informational and used to gate GST automation.</summary>
        public string Environment { get; set; } = "Production";
        /// <summary>false only for the configuration migrated from the Phase 1 single-company file (keeps its existing references valid).</summary>
        public bool ReferenceIncludesConfigId { get; set; } = true;

        // ----- connection -----
        public string SapDatabase { get; set; } = "";
        public string ErpUrl { get; set; } = "";
        /// <summary>Optional Frappe site name, sent as X-Frappe-Site-Name for multi-site self-hosted benches.</summary>
        public string ErpSite { get; set; } = "";
        public string ApiKey { get; set; } = "";
        /// <summary>DPAPI-protected, base64. Use <see cref="ApiSecret"/> to read/write.</summary>
        public string ApiSecretProtected { get; set; } = "";
        public string ErpCompany { get; set; } = "";
        public string ErpAbbr { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 60;

        // ----- automation (independent switches) -----
        public bool AutoSync { get; set; } = true;
        public bool AutoEInvoice { get; set; } = false;
        public bool AutoEWayBill { get; set; } = false;
        public int MaxAutoRetries { get; set; } = 5;
        /// <summary>Only invoices with DocEntry >= this value are auto-synchronised (0 = set on first start to "next new invoice").</summary>
        public int SyncStartDocEntry { get; set; } = 0;

        // ----- ERPNext defaults (all optional; blank = let ERPNext decide or stop with a clear message) -----
        public string SellingPriceList { get; set; } = "Standard Selling";
        public string SalesInvoiceSeries { get; set; } = "";
        public string RootWarehouse { get; set; } = "";
        public string DefaultWarehouse { get; set; } = "";
        public string DefaultCurrency { get; set; } = "";
        public string DefaultCustomerGroup { get; set; } = "";
        public string DefaultItemGroup { get; set; } = "";
        public string DefaultUom { get; set; } = "Nos";
        public string DefaultCostCenter { get; set; } = "";
        public string DefaultIncomeAccount { get; set; } = "";
        /// <summary>ERPNext requires a valuation method on stock items; configurable (FIFO / Moving Average).</summary>
        public string DefaultValuationMethod { get; set; } = "FIFO";
        public string FreightAccount { get; set; } = "";
        /// <summary>Carries the difference when ERPNext cannot hold a SAP unit price at its rate precision.</summary>
        public string RoundOffAccount { get; set; } = "";
        public int RatePrecision { get; set; } = 2;
        /// <summary>SAP tax type (INV4.staType: -100 CGST, -110 SGST, -120 IGST, ...) -> ERPNext account of the configured company. Never invented: unmapped non-zero taxes stop the sync.</summary>
        public Dictionary<string, string> TaxAccountMap { get; set; } = new Dictionary<string, string>();
        /// <summary>SAP currency code -> ISO code, for databases that store the local currency under a non-ISO code.</summary>
        public Dictionary<string, string> CurrencyMap { get; set; } = new Dictionary<string, string>();
        /// <summary>Other ERPNext companies that must never be written to (in addition to the automatic check against every other company on the site).</summary>
        public List<string> ProtectedCompanies { get; set; } = new List<string>();

        // ----- GST profile (entered by the customer; may stay incomplete without affecting invoice sync) -----
        public string SupplierGstin { get; set; } = "";
        public string LegalName { get; set; } = "";
        public string TradeName { get; set; } = "";
        public string StateCode { get; set; } = "";
        public string GstRegistrationType { get; set; } = "";
        public string GstAddress { get; set; } = "";
        public string GstPin { get; set; } = "";
        public string DefaultPlaceOfSupply { get; set; } = "";
        public bool EInvoiceEnabled { get; set; } = false;
        public bool EWayBillEnabled { get; set; } = false;
        /// <summary>Not configured / Configured (set by the GST validation, never assumed).</summary>
        public string GspStatus { get; set; } = "Not validated";

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

        /// <summary>Suffix appended to the SAP sync reference so two configurations can never collide.</summary>
        [JsonIgnore]
        public string ReferenceSuffix { get { return ReferenceIncludesConfigId && !string.IsNullOrEmpty(Id) ? "-" + Id : ""; } }

        /// <summary>Value stored in the U_ConfigId column of the add-on's UDTs ('' for the migrated Phase 1 configuration).</summary>
        [JsonIgnore]
        public string StoredConfigId { get { return ReferenceIncludesConfigId ? (Id ?? "") : ""; } }

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        /// <summary>The GST automation switches are only honoured when the GST profile has been validated.</summary>
        [JsonIgnore]
        public bool GstReady { get { return !string.IsNullOrWhiteSpace(SupplierGstin) && GspStatus == "Configured"; } }

        /// <summary>Company-level isolation: the target must be configured and must not be a protected company.</summary>
        public void ValidateTarget()
        {
            string c = (ErpCompany ?? "").Trim(), a = (ErpAbbr ?? "").Trim();
            if (c.Length == 0 || a.Length == 0) throw new InvalidOperationException("ERPNext company and abbreviation must be configured.");
            if (ProtectedCompanies.Any(p => string.Equals(p, c, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Safety stop: '" + c + "' is listed as a protected company and cannot be the target of this configuration.");
            if (string.IsNullOrWhiteSpace(SapDatabase)) throw new InvalidOperationException("The SAP company database must be configured.");
        }

        public string MapCurrency(string sapCode)
        {
            string c = (sapCode ?? "").Trim();
            string m;
            return CurrencyMap.TryGetValue(c, out m) ? m : c;
        }

        public AppConfig Clone()
        {
            var c = JsonConvert.DeserializeObject<AppConfig>(JsonConvert.SerializeObject(this));
            return c;
        }
    }

    /// <summary>The configuration file: any number of profiles; at most one active profile per SAP company database.</summary>
    public sealed class ConfigFile
    {
        public int Version { get; set; } = 2;
        public List<AppConfig> Profiles { get; set; } = new List<AppConfig>();
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

        public static ConfigFile LoadFile()
        {
            ConfigFile file = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    string text = File.ReadAllText(FilePath);
                    var root = Newtonsoft.Json.Linq.JObject.Parse(text);
                    if (root["Profiles"] != null) file = root.ToObject<ConfigFile>();
                    else if (root["ErpCompany"] != null || root["SapDatabase"] != null)
                    {
                        // Phase 1 single-company file -> migrate to profile #1, keeping its reference format so existing drafts stay valid
                        var legacy = root.ToObject<AppConfig>();
                        legacy.Id = "default"; legacy.Name = string.IsNullOrEmpty(legacy.Name) ? "Migrated configuration" : legacy.Name;
                        legacy.ReferenceIncludesConfigId = false; legacy.Active = true;
                        file = new ConfigFile();
                        file.Profiles.Add(legacy);
                        Logger.Info("config.migrated", "profile", legacy.Name);
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("config.load.failed", "error", ex.Message); }
            file = file ?? new ConfigFile();
            foreach (var p in file.Profiles)
            {
                if (string.IsNullOrEmpty(p.Id)) p.Id = AppConfig.NewId();
                Logger.RegisterSecret(p.ApiSecret); Logger.RegisterSecret(p.ApiKey);
            }
            return file;
        }

        public static void SaveFile(ConfigFile file)
        {
            var activePerDb = file.Profiles.Where(p => p.Active).GroupBy(p => (p.SapDatabase ?? "").ToLowerInvariant()).FirstOrDefault(g => g.Count() > 1);
            if (activePerDb != null) throw new InvalidOperationException("Only one active configuration is allowed per SAP company database ('" + activePerDb.Key + "').");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(file, Formatting.Indented));
            foreach (var p in file.Profiles) { Logger.RegisterSecret(p.ApiSecret); Logger.RegisterSecret(p.ApiKey); }
        }

        /// <summary>The active configuration for a SAP company database, or null (the add-on then only offers the configuration screens).</summary>
        public static AppConfig FindFor(string sapDatabase)
        {
            return LoadFile().Profiles.FirstOrDefault(p => p.Active && string.Equals(p.SapDatabase, sapDatabase, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Insert or replace a profile (matched by Id).</summary>
        public static void Upsert(AppConfig cfg)
        {
            var f = LoadFile();
            if (string.IsNullOrEmpty(cfg.Id)) cfg.Id = AppConfig.NewId();
            int i = f.Profiles.FindIndex(p => p.Id == cfg.Id);
            if (i >= 0) f.Profiles[i] = cfg; else f.Profiles.Add(cfg);
            SaveFile(f);
        }

        public static void Delete(string id)
        {
            var f = LoadFile();
            f.Profiles.RemoveAll(p => p.Id == id);
            SaveFile(f);
        }

        /// <summary>First profile (back-compat for tools/tests that expect a single configuration).</summary>
        public static AppConfig Load()
        {
            return LoadFile().Profiles.FirstOrDefault() ?? new AppConfig();
        }

        public static void Save(AppConfig cfg) { Upsert(cfg); }
    }
}
