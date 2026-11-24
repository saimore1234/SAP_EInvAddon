using System;
using System.Globalization;
using EInvGstAddon.Common;
using EInvGstAddon.Core;

namespace EInvGstAddon.UI
{
    // =====================================================================================================
    // Integration history / error log (SAP-side @EINV_TXN, scoped to the active configuration)
    // =====================================================================================================
    public sealed class HistoryForm : FormBase
    {
        private readonly bool _errorsOnly;
        public static string TypeOf(bool errors) { return errors ? "EINV_LOGS" : "EINV_HISTORY"; }

        public HistoryForm(AddonServices svc, bool errorsOnly) : base(svc)
        {
            _errorsOnly = errorsOnly;
            Create(TypeOf(errorsOnly), errorsOnly ? "Error Logs" : "Integration History", 960, 460);
            Button("bRef", "Refresh", 15, 12, 90);
            QueryGrid("grid", "DT", 15, 42, 920, 370);
            Static("lMsg", "", 15, 420, 920);
            Refresh();
        }

        private void Refresh()
        {
            string where = "WHERE ISNULL(t.U_ConfigId,'')=" + SapReader.Lit(Svc.Cfg.StoredConfigId) + (_errorsOnly ? " AND t.U_Status NOT IN ('Generated','Valid','Cancelled','Not generated') " : "");
            LoadGrid("grid", "DT", "SELECT TOP 1000 t.U_Ts AS \"Time\", i.DocNum AS \"SAP DocNum\", t.U_DocEntry AS \"DocEntry\", t.U_Operation AS \"Operation\", t.U_Status AS \"Status\", t.U_Message AS \"Message\", t.U_RequestId AS \"Request ID\", t.U_Reference AS \"Reference\" " +
                "FROM [@" + Constants.UdtTxn + "] t LEFT JOIN OINV i ON i.DocEntry=t.U_DocEntry " + where + " ORDER BY t.U_Ts DESC");
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (!e.BeforeAction && e.EventType == SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED && e.ItemUID == "bRef") Refresh();
        }
    }

    // =====================================================================================================
    // Dashboard (scoped to the active configuration)
    // =====================================================================================================
    public sealed class DashboardForm : FormBase
    {
        public const string Type = "EINV_DASHBOARD";
        private static readonly string[] Kpis =
        {
            "Total SAP Invoices", "E-Invoices Generated", "E-Invoices Pending", "E-Invoices Unknown (reconcile)", "E-Way Bills Generated", "E-Way Bills Pending", "Cancelled Documents", "GST operations in range", "GST Portal GST API"
        };

        public DashboardForm(AddonServices svc) : base(svc)
        {
            Create(Type, "E-Invoice & E-Way Bill - Dashboard", 760, 620);
            Static("lF", "From", 15, 15, 40); DateEdit("eF", 60, 12, 90);
            Static("lT", "To", 170, 15, 25); DateEdit("eT", 200, 12, 90);
            Static("lC", "Customer", 305, 15, 55); Edit("eC", 365, 12, 140);
            Button("bRef", "Refresh", 520, 12, 90);
            Static("hdr", svc.Cfg.Name + "  |  SAP " + svc.Cfg.SapDatabase + " -> integration " + svc.Cfg.IntegrationId + " (" + svc.Cfg.Environment + ")", 15, 38, 700);
            for (int i = 0; i < Kpis.Length; i++)
            {
                int col = i < 5 ? 0 : 1, row = i < 5 ? i : i - 5;
                Static("k" + i, Kpis[i], 15 + col * 370, 62 + row * 22, 190);
                Static("v" + i, "-", 210 + col * 370, 62 + row * 22, 160);
            }
            Static("g1", "Daily GST operations (last 14 days)  |  Monthly e-Invoices / e-Way Bills", 15, 190, 700);
            QueryGrid("grid", "DT", 15, 208, 345, 160);
            QueryGrid("grid2", "DT2", 370, 208, 360, 160);
            Static("g2", "GST tax summary of invoices in range (SAP values)", 15, 376, 700);
            QueryGrid("grid3", "DT3", 15, 394, 715, 150);
            Static("lMsg", "", 15, 552, 715);
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
                string docs = "FROM OINV i LEFT JOIN [@" + Constants.UdtDocs + "] d ON d.U_DocEntry=i.DocEntry AND ISNULL(d.U_ConfigId,'')=" + cfgId + " WHERE i.CANCELED='N' AND " + range;
                Caption("v0", scalar("SELECT COUNT(*) FROM OINV i WHERE i.CANCELED='N' AND " + range));
                Caption("v1", scalar("SELECT COUNT(*) " + docs + " AND d.U_EInvStatus='Generated'"));
                Caption("v2", scalar("SELECT COUNT(*) " + docs + " AND ISNULL(d.U_EInvStatus,'Not generated') IN ('Not generated','Failed')"));
                Caption("v3", scalar("SELECT COUNT(*) " + docs + " AND (d.U_EInvStatus='Unknown' OR d.U_EwbStatus='Unknown')"));
                Caption("v4", scalar("SELECT COUNT(*) " + docs + " AND d.U_EwbStatus='Generated'"));
                Caption("v5", scalar("SELECT COUNT(*) " + docs + " AND ISNULL(d.U_EwbStatus,'Not generated') IN ('Not generated','Failed')"));
                Caption("v6", scalar("SELECT COUNT(*) " + docs + " AND ISNULL(d.U_CancelSt,'')<>''"));
                Caption("v7", scalar("SELECT COUNT(*) FROM [@" + Constants.UdtTxn + "] t JOIN OINV i ON i.DocEntry=t.U_DocEntry WHERE ISNULL(t.U_ConfigId,'')=" + cfgId + " AND " + range));
                Caption("v8", "checking ...");
                LoadGrid("grid", "DT", "SELECT TOP 14 LEFT(t.U_Ts,10) AS \"Day\", COUNT(*) AS \"Operations\", SUM(CASE WHEN t.U_Status IN ('Generated','Cancelled','Valid') THEN 1 ELSE 0 END) AS \"Success\", SUM(CASE WHEN t.U_Status NOT IN ('Generated','Cancelled','Valid') THEN 1 ELSE 0 END) AS \"Failed/Other\" " +
                    "FROM [@" + Constants.UdtTxn + "] t WHERE ISNULL(t.U_ConfigId,'')=" + cfgId + " GROUP BY LEFT(t.U_Ts,10) ORDER BY 1 DESC");
                LoadGrid("grid2", "DT2", "SELECT TOP 12 LEFT(d.U_AckDate,7) AS \"Month\", SUM(CASE WHEN ISNULL(d.U_Irn,'')<>'' THEN 1 ELSE 0 END) AS \"E-Invoices\", SUM(CASE WHEN ISNULL(d.U_EwbNo,'')<>'' THEN 1 ELSE 0 END) AS \"E-Way Bills\" " +
                    "FROM [@" + Constants.UdtDocs + "] d WHERE ISNULL(d.U_ConfigId,'')=" + cfgId + " AND ISNULL(d.U_AckDate,'')<>'' GROUP BY LEFT(d.U_AckDate,7) ORDER BY 1 DESC");
                LoadGrid("grid3", "DT3", "SELECT t.staType AS \"SAP Tax Type\", MAX(t.StaCode) AS \"Tax Code\", COUNT(DISTINCT i.DocEntry) AS \"Invoices\", SUM(t.TaxSum) AS \"Tax Amount (LC)\" " +
                    "FROM OINV i JOIN INV4 t ON t.DocEntry=i.DocEntry WHERE i.CANCELED='N' AND t.TaxSum<>0 AND " + range + " GROUP BY t.staType ORDER BY 4 DESC");
                Svc.Background("dashboard-connection", () => { using (var c = Svc.NewClient()) { var g = new ERPNext.GstGateway(c); var p = g.Ping(); return "Connected as " + (string)p["user"] + (p["profile"] != null && p["profile"].Type != Newtonsoft.Json.Linq.JTokenType.Null ? ", profile OK" : ", PROFILE NOT FOUND"); } },
                    r => Caption("v8", r), ex => Caption("v8", "NOT CONNECTED: " + ex.Message));
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
