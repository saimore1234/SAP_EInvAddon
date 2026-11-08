using System;
using System.Collections.Generic;
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
    /// ERPNext Connection Settings: any number of configurations (SAP company database + ERPNext server + ERPNext company).
    /// Everything is entered here or discovered from ERPNext; nothing is preset for a particular customer.
    /// </summary>
    public sealed class SettingsForm : FormBase
    {
        public const string Type = "EINV_SETTINGS";
        private AppConfig _cur;          // profile currently shown (a working copy)
        private bool _isNew;

        public SettingsForm(AddonServices svc) : base(svc)
        {
            Create(Type, "ERPNext Connection Settings", 760, 640);
            Static("lP", "Configuration", 15, 12, 90); Combo("cP", 110, 10, 330); Button("bNew", "New", 450, 9, 60);
            int y = 38;
            // left column
            Static("l1", "Configuration name", 15, y, 120); Edit("eNm", 140, y, 230);
            Static("r1", "SAP company database", 390, y, 130); Edit("eDb", 525, y, 200); y += 22;
            Static("l2", "ERPNext base URL (https)", 15, y, 120); Edit("eUrl", 140, y, 230);
            Static("r2", "ERPNext site (optional)", 390, y, 130); Edit("eSite", 525, y, 200); y += 22;
            Static("l3", "API key", 15, y, 120); Edit("eKey", 140, y, 230);
            Static("r3", "API secret", 390, y, 130); Edit("eSec", 525, y, 200, true); y += 22;
            Static("l4", "ERPNext company", 15, y, 120); Edit("eCo", 140, y, 230);
            Static("r4", "Company abbreviation", 390, y, 130); Edit("eAb", 525, y, 100); y += 22;
            Static("l5", "Default currency", 15, y, 120); Edit("eCur", 140, y, 80);
            Static("r5", "Environment", 390, y, 130); Combo("cEnv", 525, y, 120); AddValid("cEnv", "Production", "Production"); AddValid("cEnv", "Sandbox", "Sandbox"); y += 22;
            Static("l6", "Default warehouse", 15, y, 120); Edit("eWh", 140, y, 230);
            Static("r6", "Default customer group", 390, y, 130); Edit("eCg", 525, y, 200); y += 22;
            Static("l7", "Default item group", 15, y, 120); Edit("eIg", 140, y, 230);
            Static("r7", "Default UOM", 390, y, 130); Edit("eUom", 525, y, 100); y += 22;
            Static("l8", "Default cost centre", 15, y, 120); Edit("eCc", 140, y, 230);
            Static("r8", "Default income account", 390, y, 130); Edit("eIa", 525, y, 200); y += 22;
            Static("l9", "Selling price list", 15, y, 120); Edit("ePl", 140, y, 230);
            Static("r9", "Sales Invoice naming series", 390, y, 130); Edit("eSr", 525, y, 200); y += 22;
            Static("l10", "Connection timeout (s)", 15, y, 120); Edit("eTo", 140, y, 60);
            Static("r10", "SAP admin users (comma)", 390, y, 130); Edit("eAu", 525, y, 200); y += 24;
            Check("cAuto", "Automatic invoice synchronization", 140, y, 230); Check("cAei", "Automatic E-Invoice generation", 390, y, 230); y += 18;
            Check("cAew", "Automatic E-Way Bill generation (needs validated GST configuration)", 390, y, 330); y += 24;
            Button("bTest", "Test Connection", 15, y, 120); Button("bAuth", "Test Authentication", 140, y, 130); Button("bCos", "Fetch Companies", 275, y, 120);
            Button("bWh", "Fetch Warehouses", 400, y, 120); Button("bAcc", "Fetch Accounts", 525, y, 110); y += 24;
            Button("bCg", "Customer Groups", 15, y, 120); Button("bIg", "Item Groups", 140, y, 130); Button("bSave", "Save Configuration", 275, y, 120);
            Button("bAct", "Activate / Deactivate", 400, y, 150); Button("bDel", "Delete Configuration", 555, y, 130); y += 24;
            Button("bSap", "Create SAP UDF/UDT", 15, y, 150); Button("bErp", "Create ERPNext Fields", 170, y, 150); y += 28;
            MultiLine("eLog", 15, y, 720, 150);
            FillProfiles(Svc.Cfg.Id);
        }

        private void FillProfiles(string select)
        {
            ClearCombo("cP");
            var all = ConfigStore.LoadFile().Profiles;
            foreach (var p in all) AddValid("cP", p.Id, (p.Active ? "" : "[inactive] ") + p.Name + "  |  " + p.SapDatabase + " -> " + p.ErpCompany);
            var pick = all.FirstOrDefault(p => p.Id == select) ?? all.FirstOrDefault(p => string.Equals(p.SapDatabase, Svc.Sap.DatabaseName, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
            if (pick != null) { Set("cP", pick.Id); Show(pick, false); }
            else
            {
                var fresh = new AppConfig { Id = AppConfig.NewId(), SapDatabase = Svc.Sap.DatabaseName, Active = true, Name = "" };
                Show(fresh, true);
                Log("eLog", "No configuration exists yet. The open SAP company database is '" + Svc.Sap.DatabaseName + "'. Fill in the ERPNext connection and click Save Configuration.");
            }
        }

        private void Show(AppConfig c, bool isNew)
        {
            _cur = c; _isNew = isNew;
            Set("eNm", c.Name); Set("eDb", c.SapDatabase); Set("eUrl", c.ErpUrl); Set("eSite", c.ErpSite); Set("eKey", c.ApiKey); Set("eSec", "");
            Set("eCo", c.ErpCompany); Set("eAb", c.ErpAbbr); Set("eCur", c.DefaultCurrency); Set("cEnv", c.Environment);
            Set("eWh", c.DefaultWarehouse); Set("eCg", c.DefaultCustomerGroup); Set("eIg", c.DefaultItemGroup); Set("eUom", c.DefaultUom); Set("eCc", c.DefaultCostCenter);
            Set("eIa", c.DefaultIncomeAccount); Set("ePl", c.SellingPriceList); Set("eSr", c.SalesInvoiceSeries); Set("eTo", c.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            Set("eAu", string.Join(",", c.AdminUsers)); Set("cAuto", c.AutoSync ? "Y" : "N"); Set("cAei", c.AutoEInvoice ? "Y" : "N"); Set("cAew", c.AutoEWayBill ? "Y" : "N");
        }

        private AppConfig FromForm()
        {
            var c = _cur.Clone();
            c.Name = Get("eNm"); c.SapDatabase = Get("eDb"); c.ErpUrl = Get("eUrl").TrimEnd('/'); c.ErpSite = Get("eSite"); c.ApiKey = Get("eKey");
            c.ErpCompany = Get("eCo"); c.ErpAbbr = Get("eAb"); c.DefaultCurrency = Get("eCur"); c.Environment = string.IsNullOrEmpty(Get("cEnv")) ? "Production" : Get("cEnv");
            c.DefaultWarehouse = Get("eWh"); c.DefaultCustomerGroup = Get("eCg"); c.DefaultItemGroup = Get("eIg"); c.DefaultUom = string.IsNullOrEmpty(Get("eUom")) ? "Nos" : Get("eUom");
            c.DefaultCostCenter = Get("eCc"); c.DefaultIncomeAccount = Get("eIa"); c.SellingPriceList = string.IsNullOrEmpty(Get("ePl")) ? "Standard Selling" : Get("ePl"); c.SalesInvoiceSeries = Get("eSr");
            int to; c.TimeoutSeconds = int.TryParse(Get("eTo"), out to) && to >= 5 && to <= 600 ? to : 60;
            c.AdminUsers = Get("eAu").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            c.AutoSync = Get("cAuto") == "Y";
            c.AutoEInvoice = Get("cAei") == "Y" && c.GstReady && c.EInvoiceEnabled;       // GST automation only for a validated GST configuration
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
                case "bCos": Run("Fetch Companies", c => string.Join("; ", c.GetList("Company", new[] { "name", "abbr", "default_currency" }).Select(x => (string)x["name"] + " [" + (string)x["abbr"] + ", " + (string)x["default_currency"] + "]"))); break;
                case "bWh": Run("Fetch Warehouses", c => List(c.GetList("Warehouse", new[] { "name" }, new object[] { new object[] { "company", "=", Get("eCo") }, new object[] { "is_group", "=", 0 } }, 200))); break;
                case "bAcc": Run("Fetch Accounts", c => List(c.GetList("Account", new[] { "name" }, new object[] { new object[] { "company", "=", Get("eCo") }, new object[] { "is_group", "=", 0 }, new object[] { "account_type", "in", new[] { "Tax", "Chargeable", "Income Account", "Round Off" } } }, 300))); break;
                case "bCg": Run("Customer Groups", c => List(c.GetList("Customer Group", new[] { "name" }, new object[] { new object[] { "is_group", "=", 0 } }, 100))); break;
                case "bIg": Run("Item Groups", c => List(c.GetList("Item Group", new[] { "name" }, new object[] { new object[] { "is_group", "=", 0 } }, 100))); break;
                case "bErp": Run("Create ERPNext custom fields", c => { var made = new ErpSchema(c).Ensure(); return made.Count == 0 ? "All ERPNext SAP-reference fields already exist." : "Created: " + string.Join(", ", made); }); break;
            }
        }

        private static string List(Newtonsoft.Json.Linq.JArray a) { return a.Count + " found: " + string.Join("; ", a.Select(x => (string)x["name"]).Take(60)); }

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
                if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = cfg.SapDatabase + " -> " + cfg.ErpCompany;
                cfg.ValidateTarget();
                var others = ConfigStore.LoadFile().Profiles.Where(p => p.Id != cfg.Id && p.Active && string.Equals(p.SapDatabase, cfg.SapDatabase, StringComparison.OrdinalIgnoreCase));
                if (others.Any()) cfg.Active = false;
            }
            catch (Exception ex) { Log("eLog", "Cannot save: " + ex.Message); return; }

            Log("eLog", "Validating company on the ERPNext site ...");
            Svc.Background("validate-company", () =>
            {
                using (var c = new ErpClient(cfg))
                {
                    c.Method("frappe.auth.get_logged_user");
                    var co = c.GetDoc("Company", cfg.ErpCompany);
                    if (co == null) throw new InvalidOperationException("Company '" + cfg.ErpCompany + "' does not exist on " + cfg.ErpUrl + ".");
                    if (!string.Equals((string)co["abbr"], cfg.ErpAbbr, StringComparison.Ordinal)) throw new InvalidOperationException("Abbreviation mismatch: ERPNext says '" + (string)co["abbr"] + "'.");
                    return (string)co["default_currency"];
                }
            }, currency =>
            {
                try
                {
                    if (string.IsNullOrEmpty(cfg.DefaultCurrency)) cfg.DefaultCurrency = currency;
                    ConfigStore.Upsert(cfg);
                    bool isCurrent = cfg.Id == Svc.Cfg.Id;
                    if (isCurrent && Svc.HasConfig) { JsonConvert.PopulateObject(JsonConvert.SerializeObject(cfg), Svc.Cfg); Svc.ResetEngine(); }
                    Set("eSec", ""); _cur = cfg; _isNew = false;
                    FillProfiles(cfg.Id);
                    Log("eLog", "Configuration saved" + (cfg.Active ? "" : " (inactive)") + "." + (isCurrent && Svc.HasConfig ? "" : " Restart the add-on (or reopen the company) to activate it."));
                }
                catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
            }, ex => Log("eLog", "Not saved - validation failed: " + ex.Message));
        }

        private void Delete()
        {
            if (_isNew) { Log("eLog", "Nothing to delete."); return; }
            if (Svc.App.MessageBox("Delete configuration '" + _cur.Name + "'?\nERPNext documents and SAP records are NOT deleted; only this saved connection is removed.", 2, "Delete", "Cancel", "") != 1) return;
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
