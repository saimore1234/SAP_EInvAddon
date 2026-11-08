using System;
using System.Globalization;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.Database;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json;

namespace EInvGstAddon.UI
{
    /// <summary>
    /// ERPNext Connection Settings: any number of configurations (SAP company database + ERPNext server + integration profile).
    /// Credentials here authenticate SAP to ERPNext only; the GSP credentials live in ERPNext and are never seen by SAP.
    /// </summary>
    public sealed class SettingsForm : FormBase
    {
        public const string Type = "EINV_SETTINGS";
        private AppConfig _cur;
        private bool _isNew;

        public SettingsForm(AddonServices svc) : base(svc)
        {
            Create(Type, "ERPNext Connection Settings", 720, 520);
            Static("lP", "Configuration", 15, 12, 90); Combo("cP", 110, 10, 330); Button("bNew", "New", 450, 9, 60);
            int y = 40;
            Static("l1", "Configuration name", 15, y, 130); Edit("eNm", 150, y, 210); Static("r1", "SAP company database", 380, y, 130); Edit("eDb", 515, y, 180); y += 22;
            Static("l2", "ERPNext base URL (https)", 15, y, 130); Edit("eUrl", 150, y, 210); Static("r2", "ERPNext site (optional)", 380, y, 130); Edit("eSite", 515, y, 180); y += 22;
            Static("l3", "API key", 15, y, 130); Edit("eKey", 150, y, 210); Static("r3", "API secret", 380, y, 130); Edit("eSec", 515, y, 180, true); y += 22;
            Static("l4", "Integration ID", 15, y, 130); Edit("eIid", 150, y, 210); Static("r4", "Environment", 380, y, 130); Combo("cEnv", 515, y, 120); AddValid("cEnv", "Production", "Production"); AddValid("cEnv", "Sandbox", "Sandbox"); y += 22;
            Static("l5", "Connection timeout (s)", 15, y, 130); Edit("eTo", 150, y, 60); Static("r5", "SAP admin users (comma)", 380, y, 130); Edit("eAu", 515, y, 180); y += 22;
            Static("l6", "ERPNext company (info)", 15, y, 130); Edit("eCo", 150, y, 210); Static("r6", "Document number pattern", 380, y, 130); Edit("eDn", 515, y, 180); y += 24;
            Check("cAei", "Automatic E-Invoice generation (needs validated GST configuration)", 15, y, 330); Check("cAew", "Automatic E-Way Bill generation", 380, y, 300); y += 26;
            Button("bTest", "Test Connection", 15, y, 120); Button("bAuth", "Test Authentication", 140, y, 130); Button("bProf", "Check Integration Profile", 275, y, 160); Button("bSave", "Save Configuration", 440, y, 130); y += 24;
            Button("bAct", "Activate / Deactivate", 15, y, 150); Button("bDel", "Delete Configuration", 170, y, 140); Button("bSap", "Create SAP UDF/UDT", 315, y, 150); y += 28;
            MultiLine("eLog", 15, y, 680, 190);
            FillProfiles(Svc.Cfg.Id);
        }

        private void FillProfiles(string select)
        {
            ClearCombo("cP");
            var all = ConfigStore.LoadFile().Profiles;
            foreach (var p in all) AddValid("cP", p.Id, (p.Active ? "" : "[inactive] ") + p.Name + "  |  " + p.SapDatabase + " -> " + p.IntegrationId);
            var pick = all.FirstOrDefault(p => p.Id == select) ?? all.FirstOrDefault(p => string.Equals(p.SapDatabase, Svc.Sap.DatabaseName, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
            if (pick != null) { Set("cP", pick.Id); Show(pick, false); }
            else
            {
                Show(new AppConfig { Id = AppConfig.NewId(), SapDatabase = Svc.Sap.DatabaseName, Active = true, IntegrationId = "SAPB1_" + System.Text.RegularExpressions.Regex.Replace(Svc.Sap.DatabaseName, "[^A-Za-z0-9]", "_").ToUpperInvariant() }, true);
                Log("eLog", "No configuration exists yet. The open SAP company database is '" + Svc.Sap.DatabaseName + "'. Enter the ERPNext URL, API key/secret and the Integration ID created on the ERPNext site, then Save.");
            }
        }

        private void Show(AppConfig c, bool isNew)
        {
            _cur = c; _isNew = isNew;
            Set("eNm", c.Name); Set("eDb", c.SapDatabase); Set("eUrl", c.ErpUrl); Set("eSite", c.ErpSite); Set("eKey", c.ApiKey); Set("eSec", ""); Set("eIid", c.IntegrationId); Set("cEnv", c.Environment);
            Set("eTo", c.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)); Set("eAu", string.Join(",", c.AdminUsers)); Set("eCo", c.ErpCompany); Set("eDn", c.DocNumberPattern);
            Set("cAei", c.AutoEInvoice ? "Y" : "N"); Set("cAew", c.AutoEWayBill ? "Y" : "N");
        }

        private AppConfig FromForm()
        {
            var c = _cur.Clone();
            c.Name = Get("eNm"); c.SapDatabase = Get("eDb"); c.ErpUrl = Get("eUrl").TrimEnd('/'); c.ErpSite = Get("eSite"); c.ApiKey = Get("eKey"); c.IntegrationId = Get("eIid");
            c.Environment = string.IsNullOrEmpty(Get("cEnv")) ? "Production" : Get("cEnv"); c.ErpCompany = Get("eCo"); c.DocNumberPattern = string.IsNullOrEmpty(Get("eDn")) ? "{DocNum}" : Get("eDn");
            int to; c.TimeoutSeconds = int.TryParse(Get("eTo"), out to) && to >= 5 && to <= 600 ? to : 60;
            c.AdminUsers = Get("eAu").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            c.AutoEInvoice = Get("cAei") == "Y" && c.GstReady && c.EInvoiceEnabled;                // GST automation only for a validated GST configuration
            c.AutoEWayBill = Get("cAew") == "Y" && c.GstReady && c.EWayBillEnabled;
            string sec = Get("eSec");
            if (sec.Length > 0) c.ApiSecret = sec;
            return c;
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.EventType == SAPbouiCOM.BoEventTypes.et_COMBO_SELECT && !e.BeforeAction && e.ItemUID == "cP")
            {
                var p = ConfigStore.LoadFile().Profiles.FirstOrDefault(x => x.Id == Get("cP"));
                if (p != null) { Show(p, false); Log("eLog", "Loaded configuration '" + p.Name + "'."); }
                return;
            }
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            if (!Authorization.IsAdmin(Svc)) { Log("eLog", "Only a SAP superuser or a configured administrator can change the ERPNext configuration."); return; }
            switch (e.ItemUID)
            {
                case "bNew": Show(new AppConfig { Id = AppConfig.NewId(), SapDatabase = Svc.Sap.DatabaseName, Active = false }, true); Log("eLog", "New configuration. It is saved inactive if another configuration is already active for this SAP database."); break;
                case "bSave": Save(); break;
                case "bDel": Delete(); break;
                case "bAct": Toggle(); break;
                case "bSap": InstallSap(); break;
                case "bTest": Run("Test Connection", c => "ERPNext reachable: " + (string)c.Method("ping")["message"]); break;
                case "bAuth": Run("Test Authentication", c => "Authenticated as " + (string)c.Method("frappe.auth.get_logged_user")["message"]); break;
                case "bProf": Run("Check Integration Profile", c => Describe(new GstGateway(c).Ping())); break;
            }
        }

        private static string Describe(Newtonsoft.Json.Linq.JObject p)
        {
            var prof = p["profile"];
            string s = "API user " + p["user"] + " | app " + p["app"] + " " + p["api_version"] + " | Frappe " + p["frappe"] + ", ERPNext " + p["erpnext"] + ", India Compliance " + p["india_compliance"] + " | GST Settings " + ((bool?)p["gst_sandbox_mode"] == true ? "SANDBOX" : "PRODUCTION");
            if (prof == null || prof.Type == Newtonsoft.Json.Linq.JTokenType.Null) return s + "\nPROFILE NOT FOUND on the ERPNext site (create a 'SAP Integration Profile' with this Integration ID).";
            return s + "\nProfile: enabled=" + prof["enabled"] + ", environment=" + prof["environment"] + ", SAP database=" + prof["sap_database"] + ", API user matches=" + prof["api_user_matches"] + ", environment consistent with GST Settings=" + prof["environment_consistent"];
        }

        private void Run(string what, Func<ErpClient, string> work)
        {
            AppConfig cfg;
            try { cfg = FromForm(); } catch (Exception ex) { Log("eLog", what + " not run: " + ex.Message); return; }
            if (string.IsNullOrEmpty(cfg.ErpUrl)) { Log("eLog", "Enter the ERPNext URL first."); return; }
            Log("eLog", what + " ...");
            Svc.Background(what, () => { using (var c = new ErpClient(cfg)) return work(c); }, r => Log("eLog", what + ": " + r), ex => Log("eLog", what + " FAILED: " + ex.Message));
        }

        private void Save()
        {
            AppConfig cfg;
            try
            {
                cfg = FromForm();
                if (!cfg.ErpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The ERPNext URL must start with https://");
                if (string.IsNullOrEmpty(cfg.ApiKey) || string.IsNullOrEmpty(cfg.ApiSecret)) throw new InvalidOperationException("API key and secret are required.");
                if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = cfg.SapDatabase + " -> " + cfg.IntegrationId;
                cfg.ValidateTarget();
                if (ConfigStore.LoadFile().Profiles.Any(p => p.Id != cfg.Id && p.Active && string.Equals(p.SapDatabase, cfg.SapDatabase, StringComparison.OrdinalIgnoreCase))) cfg.Active = false;
            }
            catch (Exception ex) { Log("eLog", "Cannot save: " + ex.Message); return; }

            Log("eLog", "Checking the integration profile on the ERPNext site ...");
            Svc.Background("validate-profile", () => { using (var c = new ErpClient(cfg)) return new GstGateway(c).Ping(); }, p =>
            {
                try
                {
                    var prof = p["profile"];
                    string note = "";
                    if (prof == null || prof.Type == Newtonsoft.Json.Linq.JTokenType.Null) note = " WARNING: no 'SAP Integration Profile' named " + cfg.IntegrationId + " was found on the site yet; GST actions will be refused until it exists.";
                    else if ((bool?)prof["api_user_matches"] != true) note = " WARNING: the profile is bound to a different API user.";
                    else if ((bool?)prof["environment_consistent"] != true) note = " WARNING: the profile environment does not match the ERPNext GST Settings sandbox mode.";
                    ConfigStore.Upsert(cfg);
                    if (cfg.Id == Svc.Cfg.Id && Svc.HasConfig) JsonConvert.PopulateObject(JsonConvert.SerializeObject(cfg), Svc.Cfg);
                    Set("eSec", ""); _cur = cfg; _isNew = false;
                    FillProfiles(cfg.Id);
                    Log("eLog", "Configuration saved" + (cfg.Active ? "" : " (inactive)") + "." + note + (cfg.Id == Svc.Cfg.Id && Svc.HasConfig ? "" : " Restart the add-on (or reopen the company) to activate it."));
                }
                catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
            }, ex => Log("eLog", "Not saved - the ERPNext GST API could not be reached or the credentials are wrong: " + ex.Message));
        }

        private void Delete()
        {
            if (_isNew) { Log("eLog", "Nothing to delete."); return; }
            if (Svc.App.MessageBox("Delete configuration '" + _cur.Name + "'?\nERPNext data and SAP records are NOT deleted; only this saved connection is removed.", 2, "Delete", "Cancel", "") != 1) return;
            ConfigStore.Delete(_cur.Id);
            Log("eLog", "Configuration deleted." + (_cur.Id == Svc.Cfg.Id ? " Restart the add-on." : ""));
            FillProfiles(null);
        }

        private void Toggle()
        {
            if (_isNew) { Log("eLog", "Save the configuration first."); return; }
            try
            {
                var f = ConfigStore.LoadFile();
                var p = f.Profiles.First(x => x.Id == _cur.Id);
                p.Active = !p.Active;
                ConfigStore.SaveFile(f);
                Log("eLog", "Configuration is now " + (p.Active ? "ACTIVE" : "inactive") + ". Restart the add-on to apply.");
                FillProfiles(p.Id);
            }
            catch (Exception ex) { Log("eLog", "Cannot change: " + ex.Message); }
        }

        private void InstallSap()
        {
            try
            {
                Log("eLog", "Creating SAP UDFs/UDTs (this can take a moment) ...");
                var made = new SchemaInstaller(Svc.Di).Install();
                Svc.SchemaReady = true;
                Log("eLog", made.Count == 0 ? "All SAP objects already exist." : string.Join("; ", made) + "  (log out and in again so SAP reloads the new fields)");
            }
            catch (Exception ex) { Log("eLog", "SAP install FAILED: " + ex.Message); }
        }
    }
}
