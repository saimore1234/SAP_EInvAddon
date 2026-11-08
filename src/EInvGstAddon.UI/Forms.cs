using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.Database;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.UI
{
    // =====================================================================================================
    // ERPNext Connection Settings
    // =====================================================================================================
    public sealed class SettingsForm : FormBase
    {
        public const string Type = "EINV_SETTINGS";
        public SettingsForm(AddonServices svc) : base(svc)
        {
            Create(Type, "ERPNext Connection Settings", 640, 470);
            int y = 15;
            Static("lUrl", "ERPNext URL (HTTPS)", 15, y, 150); Edit("eUrl", 175, y, 420); y += 22;
            Static("lKey", "API Key", 15, y, 150); Edit("eKey", 175, y, 420); y += 22;
            Static("lSec", "API Secret", 15, y, 150); Edit("eSec", 175, y, 420, true); y += 22;
            Static("lCo", "ERPNext Company", 15, y, 150); Edit("eCo", 175, y, 420); y += 22;
            Static("lAb", "Company Abbreviation", 15, y, 150); Edit("eAb", 175, y, 120); y += 22;
            Static("lDb", "SAP Database", 15, y, 150); Edit("eDb", 175, y, 120); y += 22;
            Static("lTo", "Timeout (seconds)", 15, y, 150); Edit("eTo", 175, y, 120); y += 22;
            Check("cAuto", "Automatic synchronisation of new A/R invoices", 175, y, 400); y += 28;
            Button("bTest", "Test Connection", 15, y, 140); Button("bAuth", "Test Authentication", 165, y, 140); Button("bCos", "Fetch Companies", 315, y, 140); Button("bDet", "Company Details", 465, y, 130); y += 26;
            Button("bSap", "Create SAP UDF/UDT", 15, y, 140); Button("bErp", "Create ERPNext Fields", 165, y, 140); Button("bSave", "Save Configuration", 465, y, 130); y += 30;
            MultiLine("eLog", 15, y, 600, 140);
            Load();
        }

        private void Load()
        {
            var c = Svc.Cfg;
            Set("eUrl", c.ErpUrl); Set("eKey", c.ApiKey); Set("eSec", ""); Set("eCo", c.ErpCompany); Set("eAb", c.ErpAbbr); Set("eDb", c.SapDatabase);
            Set("eTo", c.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)); Set("cAuto", c.AutoSync ? "Y" : "N");
            Set("eLog", string.IsNullOrEmpty(c.ApiSecretProtected) ? "No API secret saved yet." : "API secret is stored encrypted for this Windows user and is never shown.");
        }

        private AppConfig FromForm()
        {
            var c = new AppConfig
            {
                ErpUrl = Get("eUrl").TrimEnd('/'), ApiKey = Get("eKey"), ErpCompany = Get("eCo"), ErpAbbr = Get("eAb"), SapDatabase = Get("eDb"),
                AutoSync = Get("cAuto") == "Y", TaxAccountMap = Svc.Cfg.TaxAccountMap, CurrencyMap = Svc.Cfg.CurrencyMap, FreightAccount = Svc.Cfg.FreightAccount,
                RoundOffAccount = Svc.Cfg.RoundOffAccount, SalesInvoiceSeries = Svc.Cfg.SalesInvoiceSeries, SellingPriceList = Svc.Cfg.SellingPriceList,
                RootWarehouse = Svc.Cfg.RootWarehouse, DefaultCustomerGroup = Svc.Cfg.DefaultCustomerGroup, DefaultValuationMethod = Svc.Cfg.DefaultValuationMethod,
                RatePrecision = Svc.Cfg.RatePrecision, MaxAutoRetries = Svc.Cfg.MaxAutoRetries, SyncStartDocEntry = Svc.Cfg.SyncStartDocEntry
            };
            int to; c.TimeoutSeconds = int.TryParse(Get("eTo"), out to) && to >= 5 && to <= 600 ? to : 60;
            string sec = Get("eSec");
            c.ApiSecretProtected = Svc.Cfg.ApiSecretProtected;
            if (sec.Length > 0) c.ApiSecret = sec;
            return c;
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            switch (e.ItemUID)
            {
                case "bSave": Save(); break;
                case "bTest": Run("Test Connection", c => { var r = c.Method("ping"); return "ERPNext reachable: " + (string)r["message"]; }); break;
                case "bAuth": Run("Test Authentication", c => "Authenticated as " + (string)c.Method("frappe.auth.get_logged_user")["message"]); break;
                case "bCos": Run("Fetch Companies", c => string.Join("; ", c.GetList("Company", new[] { "name", "abbr" }).Select(x => (string)x["name"] + " (" + (string)x["abbr"] + ")"))); break;
                case "bDet": Run("Company Details", c =>
                    {
                        var d = c.GetDoc("Company", Get("eCo"));
                        if (d == null) throw new InvalidOperationException("Company '" + Get("eCo") + "' not found in ERPNext.");
                        return "Company " + d["name"] + " | abbr " + d["abbr"] + " | currency " + d["default_currency"] + " | GSTIN '" + d["gstin"] + "' | country " + d["country"];
                    }); break;
                case "bSap": InstallSap(); break;
                case "bErp": Run("Create ERPNext custom fields", c => { var made = new ErpSchema(c).Ensure(); return made.Count == 0 ? "All ERPNext SAP-reference fields already exist." : "Created: " + string.Join(", ", made); }); break;
            }
        }

        private void Run(string what, Func<ErpClient, string> work)
        {
            AppConfig cfg;
            try { cfg = FromForm(); cfg.ValidateTarget(); } catch (Exception ex) { Log("eLog", what + " not run: " + ex.Message); return; }
            Log("eLog", what + " ...");
            Svc.Background(what, () => { using (var c = new ErpClient(cfg)) return work(c); }, r => Log("eLog", what + ": " + r), ex => Log("eLog", what + " FAILED: " + ex.Message));
        }

        private void Save()
        {
            try
            {
                var c = FromForm();
                c.ValidateTarget();
                if (!c.ErpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The ERPNext URL must start with https://");
                if (string.IsNullOrEmpty(c.ApiKey) || string.IsNullOrEmpty(c.ApiSecret)) throw new InvalidOperationException("API key and secret are required.");
                ConfigStore.Save(c);
                Svc.Cfg.ErpUrl = c.ErpUrl; Svc.Cfg.ApiKey = c.ApiKey; Svc.Cfg.ApiSecretProtected = c.ApiSecretProtected; Svc.Cfg.ErpCompany = c.ErpCompany; Svc.Cfg.ErpAbbr = c.ErpAbbr;
                Svc.Cfg.SapDatabase = c.SapDatabase; Svc.Cfg.TimeoutSeconds = c.TimeoutSeconds; Svc.Cfg.AutoSync = c.AutoSync;
                Svc.ResetEngine();
                Set("eSec", "");
                Log("eLog", "Configuration saved.");
            }
            catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
        }

        private void InstallSap()
        {
            try
            {
                Log("eLog", "Creating SAP UDFs/UDTs (this can take a moment) ...");
                var made = new SchemaInstaller(Svc.Di).Install();
                Svc.SchemaReady = true;
                Log("eLog", made.Count == 0 ? "All SAP objects already exist." : string.Join("; ", made));
            }
            catch (Exception ex) { Log("eLog", "SAP install FAILED: " + ex.Message); }
        }
    }

    // =====================================================================================================
    // Synchronisation lists (all / pending / failed / logs / history)
    // =====================================================================================================
    public enum ListMode { All, Pending, Failed, Logs, History }

    public sealed class SyncListForm : FormBase
    {
        private readonly ListMode _mode;
        public static string TypeOf(ListMode m) { return "EINV_LIST_" + m.ToString().ToUpperInvariant(); }

        public SyncListForm(AddonServices svc, ListMode mode) : base(svc)
        {
            _mode = mode;
            string[] titles = { "Invoice Synchronization", "Pending Synchronization", "Failed Synchronization", "Sync Logs", "Integration History" };
            Create(TypeOf(mode), titles[(int)mode], 900, 460);
            Button("bRef", "Refresh", 15, 12, 90);
            if (mode != ListMode.Logs && mode != ListMode.History) Button("bRetry", "Retry Selected", 115, 12, 110);
            if (mode == ListMode.Failed) Button("bRetryAll", "Retry All", 235, 12, 90);
            if (mode == ListMode.All)
            {
                Static("lDoc", "SAP DocEntry", 360, 15, 80); Edit("eDoc", 445, 12, 90); Button("bSync", "Synchronise Invoice", 545, 12, 140);
            }
            QueryGrid("grid", "DT", 15, 42, 860, 370);
            Static("lMsg", "", 15, 420, 860);
            Refresh();
        }

        private string Where()
        {
            switch (_mode)
            {
                case ListMode.Pending: return "WHERE s.U_Status IN ('Pending','Processing')";
                case ListMode.Failed: return "WHERE s.U_Status IN ('Failed','Retry Required')";
                case ListMode.Logs: return "WHERE ISNULL(s.U_Error,'')<>''";
                case ListMode.History: return "WHERE s.U_Status='Synced'";
                default: return "";
            }
        }

        private void Refresh()
        {
            LoadGrid("grid", "DT",
                "SELECT s.Code AS \"DocEntry\", s.U_DocNum AS \"DocNum\", i.CardCode AS \"Customer\", i.DocDate AS \"Doc Date\", i.DocTotal AS \"Total\", s.U_Status AS \"Status\", " +
                "s.U_ErpInv AS \"ERPNext Invoice\", s.U_SyncTs AS \"Last Attempt\", s.U_Retry AS \"Retries\", s.U_Error AS \"Message\" " +
                "FROM [@" + Constants.UdtSync + "] s LEFT JOIN OINV i ON i.DocEntry = CAST(s.Code AS INT) " + Where() + " ORDER BY CAST(s.Code AS INT) DESC");
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            try
            {
                switch (e.ItemUID)
                {
                    case "bRef": Refresh(); break;
                    case "bRetry":
                        {
                            var v = SelectedValue("grid", "DT", "DocEntry");
                            if (v == null) { Caption("lMsg", "Select an invoice row first."); break; }
                            Svc.Coordinator.Retry(int.Parse(Convert.ToString(v), CultureInfo.InvariantCulture));
                            Caption("lMsg", "Retry queued for DocEntry " + v + ".");
                            break;
                        }
                    case "bRetryAll":
                        {
                            int n = 0;
                            foreach (var r in Svc.Store.ListRecords(SyncStatus.Failed, 500).Concat(Svc.Store.ListRecords(SyncStatus.RetryRequired, 500))) { Svc.Coordinator.Retry(r.DocEntry); n++; }
                            Caption("lMsg", n + " invoice(s) queued for retry.");
                            break;
                        }
                    case "bSync":
                        {
                            int de;
                            if (!int.TryParse(Get("eDoc"), out de) || de <= 0) { Caption("lMsg", "Enter a valid SAP DocEntry."); break; }
                            Svc.Coordinator.Retry(de);
                            Caption("lMsg", "DocEntry " + de + " queued. Click Refresh to see the result.");
                            break;
                        }
                }
            }
            catch (Exception ex) { Caption("lMsg", "Error: " + ex.Message); Logger.Error("list.action.failed", ex); }
        }
    }

    // =====================================================================================================
    // Master data mappings
    // =====================================================================================================
    public enum MapKind { Customer, Item, Warehouse }

    public sealed class MappingForm : FormBase
    {
        private readonly MapKind _kind;
        public static string TypeOf(MapKind k) { return "EINV_MAP_" + k.ToString().ToUpperInvariant(); }
        private string Udt { get { return _kind == MapKind.Customer ? Constants.UdtCustMap : _kind == MapKind.Item ? Constants.UdtItemMap : Constants.UdtWhsMap; } }

        public MappingForm(AddonServices svc, MapKind kind) : base(svc)
        {
            _kind = kind;
            Create(TypeOf(kind), kind + " Mapping (SAP -> ERPNext)", 820, 470);
            Button("bRef", "Refresh", 15, 12, 90);
            if (kind == MapKind.Warehouse)
            {
                Button("bAuto", "Auto-map SAP Warehouses", 115, 12, 160);
                Static("lS", "SAP warehouse", 295, 15, 90); Edit("eS", 385, 12, 80);
                Static("lE", "ERPNext warehouse", 475, 15, 100); Edit("eE", 580, 12, 160);
                Button("bSet", "Save", 745, 12, 60);
            }
            QueryGrid("grid", "DT", 15, 42, 780, 360);
            Static("lMsg", "", 15, 410, 780);
            Refresh();
        }

        private void Refresh()
        {
            LoadGrid("grid", "DT", "SELECT U_SapKey AS \"SAP Key\", U_ErpName AS \"ERPNext\", U_Created AS \"Created by add-on\", U_Note AS \"Note\" FROM [@" + Udt + "] ORDER BY U_SapKey");
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            switch (e.ItemUID)
            {
                case "bRef": Refresh(); break;
                case "bAuto": AutoMap(); break;
                case "bSet": SetMapping(); break;
            }
        }

        private void AutoMap()
        {
            Caption("lMsg", "Mapping SAP warehouses (creating missing ones under " + Svc.Cfg.ErpCompany + " only) ...");
            Svc.Background("auto-map-warehouses", () =>
            {
                var o = new SyncOutcome();
                var whs = new WarehouseSync(Svc.NewClient(), Svc.Store, Svc.Source);
                var done = new List<string>();
                foreach (var w in Svc.Source.ListWarehouses())
                {
                    try { done.Add(w.Code + " -> " + whs.Ensure(w.Code, o)); }
                    catch (Exception ex) { done.Add(w.Code + " FAILED: " + ex.Message); }
                }
                return string.Join("; ", done);
            }, r => { Caption("lMsg", Trunc(r, 250)); Refresh(); }, ex => Caption("lMsg", "Failed: " + ex.Message));
        }

        private void SetMapping()
        {
            string sap = Get("eS"), erp = Get("eE");
            if (sap.Length == 0 || erp.Length == 0) { Caption("lMsg", "Enter the SAP and ERPNext warehouse."); return; }
            Svc.Background("set-warehouse-map", () =>
            {
                var whs = new WarehouseSync(Svc.NewClient(), Svc.Store, Svc.Source);
                string name = whs.Validate(erp, sap);       // refuses warehouses of another company
                Svc.Store.SaveWarehouseMap(new MapEntry { SapKey = sap, ErpName = name, Created = "N", Note = "manual mapping" });
                return name;
            }, r => { Caption("lMsg", "Mapped " + sap + " -> " + r); Refresh(); }, ex => Caption("lMsg", "Not saved: " + ex.Message));
        }

        private static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }
    }

    // =====================================================================================================
    // Dashboard
    // =====================================================================================================
    public sealed class DashboardForm : FormBase
    {
        public const string Type = "EINV_DASHBOARD";
        private static readonly string[] Kpis = { "Total SAP Invoices", "Successfully Synchronized", "Pending Synchronization", "Failed Synchronization", "ERPNext Customers Created", "ERPNext Items Created", "Last Synchronization", "ERPNext Connection" };

        public DashboardForm(AddonServices svc) : base(svc)
        {
            Create(Type, "E-Invoice & ERPNext Integration - Dashboard", 560, 380);
            Static("lF", "From", 15, 15, 40); DateEdit("eF", 60, 12, 90);
            Static("lT", "To", 170, 15, 25); DateEdit("eT", 200, 12, 90);
            Button("bRef", "Refresh", 310, 12, 90);
            for (int i = 0; i < Kpis.Length; i++)
            {
                Static("k" + i, Kpis[i], 30, 55 + i * 30, 230);
                Static("v" + i, "-", 270, 55 + i * 30, 260);
            }
            Static("lMsg", "", 15, 310, 520);
            Set("eF", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyyMMdd"));
            Set("eT", DateTime.Today.ToString("yyyyMMdd"));
            Refresh();
        }

        private void Refresh()
        {
            string f = Get("eF"), t = Get("eT");
            if (f.Length != 8) f = "19000101";
            if (t.Length != 8) t = "20991231";
            string range = "i.DocDate BETWEEN '" + f + "' AND '" + t + "'";
            try
            {
                var src = Svc.Source;
                Func<string, string> scalar = sql => Convert.ToString(Svc.Di.Scalar(sql), CultureInfo.InvariantCulture);
                Caption("v0", scalar("SELECT COUNT(*) FROM OINV i WHERE i.CANCELED='N' AND " + range));
                string byStatus = "SELECT COUNT(*) FROM [@" + Constants.UdtSync + "] s JOIN OINV i ON i.DocEntry=CAST(s.Code AS INT) WHERE " + range + " AND s.U_Status";
                Caption("v1", scalar(byStatus + "='Synced'"));
                Caption("v2", scalar(byStatus + " IN ('Pending','Processing')"));
                Caption("v3", scalar(byStatus + " IN ('Failed','Retry Required')"));
                Caption("v4", scalar("SELECT COUNT(*) FROM [@" + Constants.UdtCustMap + "] WHERE U_Created='Y'"));
                Caption("v5", scalar("SELECT COUNT(*) FROM [@" + Constants.UdtItemMap + "] WHERE U_Created='Y'"));
                string last = scalar("SELECT MAX(U_SyncTs) FROM [@" + Constants.UdtSync + "] WHERE U_Status='Synced'");
                Caption("v6", string.IsNullOrEmpty(last) ? "never" : last);
                Caption("v7", "checking ...");
                var cfg = Svc.Cfg;
                Svc.Background("dashboard-connection", () =>
                {
                    using (var c = Svc.NewClient()) return "Connected as " + (string)c.Method("frappe.auth.get_logged_user")["message"];
                }, r => Caption("v7", r), ex => Caption("v7", "NOT CONNECTED: " + ex.Message));
                Caption("lMsg", "SAP database " + cfg.SapDatabase + " -> ERPNext company " + cfg.ErpCompany + " (" + cfg.ErpAbbr + ")");
            }
            catch (Exception ex) { Caption("lMsg", "Dashboard error: " + ex.Message); Logger.Warn("dashboard.failed", "error", ex.Message); }
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (!e.BeforeAction && e.EventType == SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED && e.ItemUID == "bRef") Refresh();
        }
    }
}
