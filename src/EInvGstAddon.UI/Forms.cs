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
            string cfg = "WHERE ISNULL(s.U_ConfigId,'')=" + SapReader.Lit(Svc.Cfg.StoredConfigId);
            switch (_mode)
            {
                case ListMode.Pending: return cfg + " AND s.U_Status IN ('Pending','Processing')";
                case ListMode.Failed: return cfg + " AND s.U_Status IN ('Failed','Retry Required')";
                case ListMode.Logs: return cfg + " AND ISNULL(s.U_Error,'')<>''";
                case ListMode.History: return cfg + " AND s.U_Status='Synced'";
                default: return cfg;
            }
        }

        private void Refresh()
        {
            LoadGrid("grid", "DT",
                "SELECT s.Code AS \"DocEntry\", s.U_DocNum AS \"DocNum\", i.CardCode AS \"Customer\", i.DocDate AS \"Doc Date\", i.DocTotal AS \"Total\", s.U_Status AS \"Status\", " +
                "s.U_ErpInv AS \"ERPNext Invoice\", s.U_SyncTs AS \"Last Attempt\", s.U_Retry AS \"Retries\", s.U_Error AS \"Message\" " +
                "FROM [@" + Constants.UdtSync + "] s LEFT JOIN OINV i ON i.DocEntry = s.U_DocEntry " + Where() + " ORDER BY s.U_DocEntry DESC");
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
            LoadGrid("grid", "DT", "SELECT U_SapKey AS \"SAP Key\", U_ErpName AS \"ERPNext\", U_Created AS \"Created by add-on\", U_Note AS \"Note\" FROM [@" + Udt + "] WHERE ISNULL(U_ConfigId,'')=" + SapReader.Lit(Svc.Cfg.StoredConfigId) + " ORDER BY U_SapKey");
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
    // Dashboard (scoped to the active configuration)
    // =====================================================================================================
    public sealed class DashboardForm : FormBase
    {
        public const string Type = "EINV_DASHBOARD";
        private static readonly string[] Kpis =
        {
            "Total SAP Invoices", "ERPNext Synchronized", "Pending Synchronization", "Failed Synchronization",
            "E-Invoices Generated", "E-Invoices Pending", "E-Way Bills Generated", "E-Way Bills Pending", "Cancelled Documents",
            "ERPNext Customers Created", "ERPNext Items Created", "Last Synchronization", "ERPNext Connection"
        };

        public DashboardForm(AddonServices svc) : base(svc)
        {
            Create(Type, "E-Invoice & ERPNext Integration - Dashboard", 760, 640);
            Static("lF", "From", 15, 15, 40); DateEdit("eF", 60, 12, 90);
            Static("lT", "To", 170, 15, 25); DateEdit("eT", 200, 12, 90);
            Static("lC", "Customer", 305, 15, 55); Edit("eC", 365, 12, 140);
            Button("bRef", "Refresh", 520, 12, 90);
            Static("hdr", svc.Cfg.Name + "  |  SAP " + svc.Cfg.SapDatabase + " -> " + svc.Cfg.ErpCompany + " (" + svc.Cfg.Environment + ")", 15, 38, 700);
            for (int i = 0; i < Kpis.Length; i++)
            {
                int col = i < 7 ? 0 : 1, row = i < 7 ? i : i - 7;
                Static("k" + i, Kpis[i], 15 + col * 370, 62 + row * 22, 180);
                Static("v" + i, "-", 200 + col * 370, 62 + row * 22, 170);
            }
            Static("g1", "Daily invoice synchronization (last 14 days) / Monthly e-invoices & e-way bills", 15, 226, 700);
            QueryGrid("grid", "DT", 15, 244, 345, 150);
            QueryGrid("grid2", "DT2", 370, 244, 360, 150);
            Static("g2", "GST tax summary (synchronized invoices in range)", 15, 402, 700);
            QueryGrid("grid3", "DT3", 15, 420, 715, 120);
            Static("lMsg", "", 15, 548, 715);
            Set("eF", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyyMMdd"));
            Set("eT", DateTime.Today.ToString("yyyyMMdd"));
            Refresh();
        }

        private void Refresh()
        {
            string f = Get("eF"), t = Get("eT");
            if (f.Length != 8) f = "19000101";
            if (t.Length != 8) t = "20991231";
            string cfgId = SapReader.Lit(Svc.Cfg.StoredConfigId);
            string cust = Get("eC").Replace("'", "''");
            string range = "i.DocDate BETWEEN '" + f + "' AND '" + t + "'" + (cust.Length > 0 ? " AND (i.CardName LIKE '%" + cust + "%' OR i.CardCode LIKE '%" + cust + "%')" : "");
            try
            {
                Func<string, string> scalar = sql => Convert.ToString(Svc.Di.Scalar(sql), CultureInfo.InvariantCulture);
                string sync = "FROM [@" + Constants.UdtSync + "] s JOIN OINV i ON i.DocEntry=s.U_DocEntry WHERE ISNULL(s.U_ConfigId,'')=" + cfgId + " AND " + range;
                string docs = "FROM [@" + Constants.UdtSync + "] s JOIN OINV i ON i.DocEntry=s.U_DocEntry LEFT JOIN [@" + Constants.UdtDocs + "] d ON d.U_DocEntry=s.U_DocEntry AND ISNULL(d.U_ConfigId,'')=ISNULL(s.U_ConfigId,'') WHERE ISNULL(s.U_ConfigId,'')=" + cfgId + " AND s.U_Status='Synced' AND " + range;
                Caption("v0", scalar("SELECT COUNT(*) FROM OINV i WHERE i.CANCELED='N' AND " + range));
                Caption("v1", scalar("SELECT COUNT(*) " + sync + " AND s.U_Status='Synced'"));
                Caption("v2", scalar("SELECT COUNT(*) " + sync + " AND s.U_Status IN ('Pending','Processing')"));
                Caption("v3", scalar("SELECT COUNT(*) " + sync + " AND s.U_Status IN ('Failed','Retry Required')"));
                Caption("v4", scalar("SELECT COUNT(*) " + docs + " AND d.U_EInvStatus='Generated'"));
                Caption("v5", scalar("SELECT COUNT(*) " + docs + " AND ISNULL(d.U_EInvStatus,'Not generated')<>'Generated'"));
                Caption("v6", scalar("SELECT COUNT(*) " + docs + " AND d.U_EwbStatus='Generated'"));
                Caption("v7", scalar("SELECT COUNT(*) " + docs + " AND ISNULL(d.U_EwbStatus,'Not generated')<>'Generated'"));
                Caption("v8", scalar("SELECT COUNT(*) " + docs + " AND ISNULL(d.U_CancelSt,'')<>''"));
                Caption("v9", scalar("SELECT COUNT(*) FROM [@" + Constants.UdtCustMap + "] WHERE ISNULL(U_ConfigId,'')=" + cfgId + " AND U_Created='Y'"));
                Caption("v10", scalar("SELECT COUNT(*) FROM [@" + Constants.UdtItemMap + "] WHERE ISNULL(U_ConfigId,'')=" + cfgId + " AND U_Created='Y'"));
                string last = scalar("SELECT MAX(U_SyncTs) FROM [@" + Constants.UdtSync + "] WHERE ISNULL(U_ConfigId,'')=" + cfgId + " AND U_Status='Synced'");
                Caption("v11", string.IsNullOrEmpty(last) ? "never" : last);
                Caption("v12", "checking ...");

                LoadGrid("grid", "DT", "SELECT TOP 14 LEFT(s.U_SyncTs,10) AS \"Day\", COUNT(*) AS \"Invoices\", SUM(CASE WHEN s.U_Status='Synced' THEN 1 ELSE 0 END) AS \"Success\", SUM(CASE WHEN s.U_Status IN ('Failed','Retry Required') THEN 1 ELSE 0 END) AS \"Failed\" " +
                    "FROM [@" + Constants.UdtSync + "] s WHERE ISNULL(s.U_ConfigId,'')=" + cfgId + " AND ISNULL(s.U_SyncTs,'')<>'' GROUP BY LEFT(s.U_SyncTs,10) ORDER BY 1 DESC");
                LoadGrid("grid2", "DT2", "SELECT TOP 12 LEFT(d.U_AckDate,7) AS \"Month\", COUNT(*) AS \"E-Invoices\", SUM(CASE WHEN ISNULL(d.U_EwbNo,'')<>'' THEN 1 ELSE 0 END) AS \"E-Way Bills\" " +
                    "FROM [@" + Constants.UdtDocs + "] d WHERE ISNULL(d.U_ConfigId,'')=" + cfgId + " AND ISNULL(d.U_Irn,'')<>'' GROUP BY LEFT(d.U_AckDate,7) ORDER BY 1 DESC");
                LoadGrid("grid3", "DT3", "SELECT t.staType AS \"SAP Tax Type\", MAX(t.StaCode) AS \"Tax Code\", COUNT(DISTINCT s.U_DocEntry) AS \"Invoices\", SUM(t.TaxSum) AS \"Tax Amount (LC)\" " +
                    "FROM [@" + Constants.UdtSync + "] s JOIN OINV i ON i.DocEntry=s.U_DocEntry JOIN INV4 t ON t.DocEntry=s.U_DocEntry WHERE ISNULL(s.U_ConfigId,'')=" + cfgId + " AND s.U_Status='Synced' AND t.TaxSum<>0 AND " + range + " GROUP BY t.staType ORDER BY 4 DESC");
                Svc.Background("dashboard-connection", () =>
                {
                    using (var c = Svc.NewClient()) return "Connected as " + (string)c.Method("frappe.auth.get_logged_user")["message"];
                }, r => Caption("v12", r), ex => Caption("v12", "NOT CONNECTED: " + ex.Message));
                Caption("lMsg", "Filters apply to the SAP posting date and customer. Counts come from the active configuration only.");
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
