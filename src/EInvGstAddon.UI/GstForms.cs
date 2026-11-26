using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;

namespace EInvGstAddon.UI
{
    // =====================================================================================================
    // E-Invoice / E-Way Bill screens: pick a SAP invoice, then run exactly the same actions as the right-click menu
    // =====================================================================================================
    public sealed class GstLauncherForm : FormBase
    {
        private readonly bool _ewb;
        private InvoiceContext _ctx;
        public static string TypeOf(bool ewb) { return ewb ? "EINV_EWAYBILL" : "EINV_EINVOICE"; }

        public GstLauncherForm(AddonServices svc, bool ewb) : base(svc)
        {
            _ewb = ewb;
            Create(TypeOf(ewb), ewb ? "Generate E-Way Bill" : "Generate E-Invoice", 640, 400);
            Static("l0", "SAP DocNum", 15, 12, 90); Edit("eNum", 110, 10, 90); Static("l1", "or DocEntry", 215, 12, 70); Edit("eEnt", 290, 10, 90); Button("bLoad", "Load", 395, 9, 70);
            string[] k = ewb ? new[] { "SAP Invoice / customer", "Status", "E-Way Bill No", "Date", "Valid until", "Vehicle", "Last refresh" } : new[] { "SAP Invoice / customer", "Status", "IRN", "Ack No", "Ack Date", "Cancellation", "Last refresh" };
            for (int i = 0; i < k.Length; i++) { Static("k" + i, k[i], 15, 42 + i * 19, 150); Static("v" + i, "", 170, 42 + i * 19, 440); }
            int y = 42 + k.Length * 19 + 10;
            var acts = ewb
                ? new[] { CtxAction.ValidateEwb, CtxAction.GenerateEwb, CtxAction.RefreshEwb, CtxAction.ViewEwb, CtxAction.UpdateVehicle, CtxAction.UpdateTransporter, CtxAction.DownloadEwb, CtxAction.PrintEwb, CtxAction.CancelEwb }
                : new[] { CtxAction.ValidateEInv, CtxAction.GenerateEInv, CtxAction.RefreshIrn, CtxAction.ViewIrn, CtxAction.ViewAck, CtxAction.ViewQr, CtxAction.DownloadSigned, CtxAction.PrintEInv, CtxAction.CancelEInv };
            for (int i = 0; i < acts.Length; i++)
            {
                var it = CtxMenuDefinition.Items.First(x => x.Action == acts[i]);
                Button("a" + (int)acts[i], it.Caption, 15 + (i % 3) * 200, y + (i / 3) * 26, 190);
            }
            y += ((acts.Length + 2) / 3) * 26 + 8;
            Static("lMsg", "These buttons run the same actions as the A/R Invoice right-click menu. Nothing is created in GST Portal besides the GST transaction record.", 15, y, 600);
        }

        private void Load()
        {
            int n, e; int.TryParse(Get("eNum"), out n); int.TryParse(Get("eEnt"), out e);
            Dictionary<string, object> r = null;
            if (e > 0) r = Svc.Source.Query1("SELECT DocEntry,DocNum,CardName,CANCELED FROM OINV WHERE DocEntry=" + e);
            else if (n > 0) r = Svc.Source.Query1("SELECT TOP 1 DocEntry,DocNum,CardName,CANCELED FROM OINV WHERE DocNum=" + n + " AND CANCELED='N' ORDER BY DocEntry DESC");
            if (r == null) { Caption("lMsg", "Invoice not found. Enter a SAP DocNum or DocEntry."); _ctx = null; return; }
            if (Convert.ToString(r["CANCELED"]) != "N") { Caption("lMsg", "That SAP invoice is cancelled."); _ctx = null; return; }
            _ctx = new InvoiceContext { FormUid = "launcher", Database = Svc.Cfg.SapDatabase, DocEntry = Convert.ToInt32(r["DocEntry"], CultureInfo.InvariantCulture), DocNum = Convert.ToInt32(r["DocNum"], CultureInfo.InvariantCulture) };
            Caption("v0", "SAP " + _ctx.DocNum + " (DocEntry " + _ctx.DocEntry + ") - " + Convert.ToString(r["CardName"]));
            var g = Svc.GstStore.GetGst(_ctx.DocEntry);
            if (_ewb) { Caption("v1", g == null ? "Not generated" : g.EwbStatus); Caption("v2", g == null ? "" : g.EwbNo); Caption("v3", g == null ? "" : g.EwbDate); Caption("v4", g == null ? "" : g.EwbValid); Caption("v5", g == null ? "" : g.Vehicle); }
            else { Caption("v1", g == null ? "Not generated" : g.EInvStatus); Caption("v2", g == null ? "" : g.Irn); Caption("v3", g == null ? "" : g.AckNo); Caption("v4", g == null ? "" : g.AckDate); Caption("v5", g == null ? "" : g.CancelStatus); }
            Caption("v6", g == null || !g.LastSync.HasValue ? "never (use Refresh)" : g.LastSync.Value.ToString("yyyy-MM-dd HH:mm"));
            Caption("lMsg", "Loaded. Use the buttons below.");
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            if (e.ItemUID == "bLoad") { Load(); return; }
            if (e.ItemUID.StartsWith("a", StringComparison.Ordinal) && e.ItemUID.Length > 1)
            {
                int code;
                if (!int.TryParse(e.ItemUID.Substring(1), out code)) return;
                if (_ctx == null) { Caption("lMsg", "Load a SAP invoice first."); return; }
                Svc.InvoiceMenu.RunAction((CtxAction)code, _ctx);
            }
        }
    }

    // =====================================================================================================
    // Registers
    // =====================================================================================================
    public sealed class RegisterForm : FormBase
    {
        private readonly bool _ewb;
        public static string TypeOf(bool ewb) { return ewb ? "EINV_REG_EWB" : "EINV_REG_EINV"; }

        public RegisterForm(AddonServices svc, bool ewb) : base(svc)
        {
            _ewb = ewb;
            Create(TypeOf(ewb), ewb ? "E-Way Bill Register" : "E-Invoice Register", 1000, 520);
            Static("l1", "Customer / text", 15, 14, 90); Edit("eC", 110, 12, 150);
            Static("l2", "From", 275, 14, 35); DateEdit("eF", 315, 12, 80); Static("l3", "To", 405, 14, 20); DateEdit("eT", 430, 12, 80);
            Static("l4", "Status", 520, 14, 40); Combo("cS", 565, 12, 120);
            AddValid("cS", "", "(all)");
            foreach (var s in new[] { "Not generated", "Generated", "Cancelled", "Unknown" }) AddValid("cS", s, s);
            Button("bSearch", "Search", 700, 10, 70); Button("bRef", "Refresh Status", 775, 10, 100); Button("bExp", "Export to Excel", 880, 10, 100);
            Button("bOpen", ewb ? "Open E-Way Bill Screen" : "Open E-Invoice Screen", 15, 38, 170); Button("bSap", "View SAP Document", 190, 38, 150);
            QueryGrid("grid", "DT", 15, 66, 965, 400);
            Static("lMsg", "", 15, 475, 965);
            Search();
        }

        private void Search()
        {
            var w = new StringBuilder("WHERE i.CANCELED='N'");
            string c = Get("eC").Replace("'", "''");
            if (c.Length > 0) w.Append(" AND (ISNULL(d.U_Customer,'') LIKE '%" + c + "%' OR ISNULL(i.CardName,'') LIKE '%" + c + "%' OR CAST(i.DocNum AS VARCHAR(20)) LIKE '%" + c + "%')");
            string f = Get("eF"), t = Get("eT");
            if (f.Length == 8) w.Append(" AND i.DocDate>='" + f + "'");
            if (t.Length == 8) w.Append(" AND i.DocDate<='" + t + "'");
            string st = Get("cS");
            if (st.Length > 0) w.Append(" AND ISNULL(" + (_ewb ? "d.U_EwbStatus" : "d.U_EInvStatus") + ",'Not generated')='" + st.Replace("'", "''") + "'");
            string cols = _ewb
                ? "i.DocNum AS \"SAP DocNum\", i.CardName AS \"Customer\", d.U_EwbNo AS \"E-Way Bill No\", d.U_EwbDate AS \"E-Way Bill Date\", d.U_EwbValid AS \"Valid Until\", d.U_Transpr AS \"Transporter\", d.U_Vehicle AS \"Vehicle\", ISNULL(d.U_EwbStatus,'Not generated') AS \"Status\", i.DocEntry AS \"DocEntry\""
                : "i.DocNum AS \"SAP DocNum\", i.CardName AS \"Customer\", d.U_Gstin AS \"GSTIN\", i.DocDate AS \"Invoice Date\", d.U_Taxable AS \"Taxable Amount\", d.U_GstAmt AS \"GST Amount\", d.U_Irn AS \"IRN\", d.U_AckNo AS \"Ack No\", d.U_AckDate AS \"Ack Date\", ISNULL(d.U_EInvStatus,'Not generated') AS \"Status\", i.DocEntry AS \"DocEntry\"";
            LoadGrid("grid", "DT", "SELECT TOP 1000 " + cols + " FROM OINV i LEFT JOIN [@" + Constants.UdtDocs + "] d ON d.U_DocEntry=i.DocEntry AND ISNULL(d.U_ConfigId,'')=" + SapReader.Lit(Svc.Cfg.StoredConfigId) + " " + w + " ORDER BY i.DocEntry DESC");
        }

        private InvoiceContext Selected()
        {
            var v = SelectedValue("grid", "DT", "DocEntry"); var n = SelectedValue("grid", "DT", "SAP DocNum");
            int de, dn; if (v == null || !int.TryParse(Convert.ToString(v), out de)) { Caption("lMsg", "Select a row first."); return null; }
            int.TryParse(Convert.ToString(n), out dn);
            return new InvoiceContext { FormUid = "register", Database = Svc.Cfg.SapDatabase, DocEntry = de, DocNum = dn };
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            try
            {
                switch (e.ItemUID)
                {
                    case "bSearch": Search(); break;
                    case "bExp": Export(); break;
                    case "bSap": { var c = Selected(); if (c != null) Svc.App.OpenForm(SAPbouiCOM.BoFormObjectEnum.fo_Invoice, "", c.DocEntry.ToString(CultureInfo.InvariantCulture)); break; }
                    case "bOpen":
                        {
                            var c = Selected(); if (c == null) break;
                            Svc.Router.Open(GstLauncherForm.TypeOf(_ewb), () => new GstLauncherForm(Svc, _ewb));
                            Caption("lMsg", "Opened the screen: enter DocEntry " + c.DocEntry + " there, or use the right-click menu on the invoice.");
                            break;
                        }
                    case "bRef":
                        {
                            var c = Selected(); if (c == null) break;
                            Svc.InvoiceMenu.RunAction(_ewb ? CtxAction.RefreshEwb : CtxAction.RefreshIrn, c);
                            Caption("lMsg", "Refresh started for SAP " + c.DocNum + "; search again when it has finished.");
                            break;
                        }
                }
            }
            catch (Exception ex) { Caption("lMsg", "Error: " + ex.Message); Logger.Error("register.failed", ex); }
        }

        private void Export()
        {
            var dt = Form.DataSources.DataTables.Item("DT");
            var sb = new StringBuilder();
            for (int c = 0; c < dt.Columns.Count; c++) sb.Append(c > 0 ? "," : "").Append(Csv(dt.Columns.Item(c).Name));
            sb.AppendLine();
            for (int r = 0; r < dt.Rows.Count; r++)
            {
                for (int c = 0; c < dt.Columns.Count; c++) sb.Append(c > 0 ? "," : "").Append(Csv(Convert.ToString(dt.GetValue(c, r), CultureInfo.InvariantCulture)));
                sb.AppendLine();
            }
            using (var dlg = new SaveFileDialog { FileName = (_ewb ? "ewaybill-register-" : "einvoice-register-") + DateTime.Now.ToString("yyyyMMdd") + ".csv", Filter = "CSV (opens in Excel)|*.csv" })
            { if (dlg.ShowDialog() != DialogResult.OK) return; File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); Caption("lMsg", "Exported " + dt.Rows.Count + " rows to " + dlg.FileName); }
        }

        private static string Csv(string s) { s = s ?? ""; return s.Contains(",") || s.Contains("\"") || s.Contains("\n") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
    }

    // =====================================================================================================
    // GST configuration (seller data entered by the customer; may stay incomplete without affecting anything else)
    // =====================================================================================================
    public sealed class GstConfigForm : FormBase
    {
        public const string Type = "EINV_GSTCFG";
        private static readonly string[] RegTypes = { "Regular", "Composition", "SEZ", "Other" };

        public GstConfigForm(AddonServices svc) : base(svc)
        {
            Create(Type, "GST Configuration", 640, 560);
            int y = 14;
            Static("a1", "Seller GSTIN", 15, y, 150); Edit("eG", 175, y, 200); y += 22;
            Static("a2", "Legal Name", 15, y, 150); Edit("eL", 175, y, 420); y += 22;
            Static("a3", "Trade Name", 15, y, 150); Edit("eT", 175, y, 420); y += 22;
            Static("a4", "State Code (2 digits)", 15, y, 150); Edit("eS", 175, y, 60); y += 22;
            Static("a5", "GST Registration Type", 15, y, 150); Combo("cR", 175, y, 150); foreach (var r in RegTypes) AddValid("cR", r, r); y += 22;
            Static("a6", "Address", 15, y, 150); Edit("eA", 175, y, 420); y += 22;
            Static("a6b", "City / Location", 15, y, 150); Edit("eCity", 175, y, 200); y += 22;
            Static("a7", "PIN Code", 15, y, 150); Edit("eP", 175, y, 100); y += 22;
            Static("a8", "Default Place of Supply", 15, y, 150); Edit("ePos", 175, y, 200); y += 22;
            Static("a9", "Environment", 15, y, 150); Combo("cE", 175, y, 120); AddValid("cE", "Sandbox", "Sandbox"); AddValid("cE", "Production", "Production"); y += 22;
            Check("kI", "E-Invoice enabled", 175, y, 200); y += 20;
            Check("kW", "E-Way Bill enabled", 175, y, 200); y += 20;
            Check("kAI", "Automatic E-Invoice generation (after validation)", 175, y, 380); y += 20;
            Check("kAW", "Automatic E-Way Bill generation (after validation)", 175, y, 380); y += 24;
            Static("a10", "GSP / ASP status", 15, y, 150); Static("vS", "", 175, y, 420); y += 24;
            Button("bVal", "Validate against GST Portal", 15, y, 170); Button("bSave", "Save", 190, y, 90); y += 28;
            MultiLine("eLog", 15, y, 600, 110);
            Load();
        }

        private void Load()
        {
            var c = Svc.Cfg;
            Set("eG", c.SupplierGstin); Set("eL", c.LegalName); Set("eT", c.TradeName); Set("eS", c.StateCode); Set("cR", string.IsNullOrEmpty(c.GstRegistrationType) ? "Regular" : c.GstRegistrationType);
            Set("eA", c.GstAddress); Set("eCity", c.GstCity); Set("eP", c.GstPin); Set("ePos", c.DefaultPlaceOfSupply); Set("cE", c.Environment);
            Set("kI", c.EInvoiceEnabled ? "Y" : "N"); Set("kW", c.EWayBillEnabled ? "Y" : "N"); Set("kAI", c.AutoEInvoice ? "Y" : "N"); Set("kAW", c.AutoEWayBill ? "Y" : "N");
            Caption("vS", c.GspStatus);
            Set("eLog", "The GSP credentials are configured in GST Portal (GST Settings), never here. Validation checks the integration profile, the GSTIN and the sandbox/production consistency on the GST Portal server.");
        }

        private string Problems()
        {
            var p = new List<string>();
            string g = Get("eG").ToUpperInvariant();
            if (g.Length > 0)
            {
                if (!Regex.IsMatch(g, @"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")) p.Add("GSTIN format is invalid.");
                else if (Get("eS").Length == 2 && g.Substring(0, 2) != Get("eS")) p.Add("State code does not match the first two digits of the GSTIN.");
            }
            if (Get("eS").Length > 0 && !Regex.IsMatch(Get("eS"), @"^[0-9]{2}$")) p.Add("State code must be 2 digits.");
            if (Get("eP").Length > 0 && !Regex.IsMatch(Get("eP"), @"^[1-9][0-9]{5}$")) p.Add("PIN code must be 6 digits.");
            if ((Get("kAI") == "Y" || Get("kAW") == "Y") && Svc.Cfg.GspStatus != "Configured") p.Add("Automatic generation can only be enabled after the configuration has been validated.");
            return string.Join(" ", p);
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            if (!Authorization.IsAdmin(Svc)) { Log("eLog", "Only a configuration administrator can change the GST configuration."); return; }
            switch (e.ItemUID)
            {
                case "bSave": Save(); break;
                case "bVal":
                    {
                        string pr = Problems(); if (pr.Length > 0) { Log("eLog", pr); return; }
                        var tmp = Svc.Cfg.Clone(); tmp.SupplierGstin = Get("eG").ToUpperInvariant(); tmp.Environment = Get("cE");
                        string gstin = tmp.SupplierGstin;
                        Log("eLog", "Validating against GST Portal ...");
                        Svc.Background("gst-validate-config", () => { using (var c = new ErpClient(tmp)) return new GstGateway(c).Ping(); }, p =>
                        {
                            var errs = new List<string>();
                            var prof = p["profile"];
                            if (string.IsNullOrEmpty(gstin)) errs.Add("Enter the seller GSTIN.");
                            if (prof == null || prof.Type == Newtonsoft.Json.Linq.JTokenType.Null) errs.Add("No integration profile named " + tmp.IntegrationId + " exists on the GST Portal server.");
                            else
                            {
                                if ((bool?)prof["enabled"] != true) errs.Add("The integration profile is disabled.");
                                if ((bool?)prof["api_user_matches"] != true) errs.Add("The integration profile is bound to a different API user.");
                                if ((bool?)prof["environment_consistent"] != true) errs.Add("The profile environment (" + prof["environment"] + ") does not match the GST Portal Settings sandbox mode.");
                                if (!string.Equals((string)prof["environment"], tmp.Environment, StringComparison.OrdinalIgnoreCase)) errs.Add("The environment here (" + tmp.Environment + ") differs from the profile (" + prof["environment"] + ").");
                                if (!string.Equals((string)prof["sap_database"], tmp.SapDatabase, StringComparison.OrdinalIgnoreCase)) errs.Add("The profile belongs to another SAP database (" + prof["sap_database"] + ").");
                            }
                            string ic = (string)p["india_compliance"];
                            if (string.IsNullOrEmpty(ic)) errs.Add("The GST service is not installed on the GST Portal server.");
                            else if (!ic.StartsWith("15.", StringComparison.Ordinal)) errs.Add("GST service version " + ic + " is not supported (15.x verified).");
                            Svc.Cfg.GspStatus = errs.Count == 0 ? "Configured" : "Not validated";
                            Caption("vS", Svc.Cfg.GspStatus);
                            Log("eLog", errs.Count == 0 ? "Validation OK (GST service " + ic + ", GST Settings " + ((bool?)p["gst_sandbox_mode"] == true ? "sandbox" : "production") + ")." : "Validation FAILED: " + string.Join("; ", errs));
                        }, ex => Log("eLog", "Validation error: " + ex.Message));
                        break;
                    }
            }
        }

        private void Save()
        {
            string pr = Problems(); if (pr.Length > 0) { Log("eLog", pr); return; }
            var c = Svc.Cfg;
            c.SupplierGstin = Get("eG").ToUpperInvariant(); c.LegalName = Get("eL"); c.TradeName = Get("eT"); c.StateCode = Get("eS"); c.GstRegistrationType = Get("cR");
            c.GstAddress = Get("eA"); c.GstCity = Get("eCity"); c.GstPin = Get("eP"); c.DefaultPlaceOfSupply = Get("ePos"); c.Environment = string.IsNullOrEmpty(Get("cE")) ? "Production" : Get("cE");
            c.EInvoiceEnabled = Get("kI") == "Y" && c.GspStatus == "Configured"; c.EWayBillEnabled = Get("kW") == "Y" && c.GspStatus == "Configured";
            c.AutoEInvoice = Get("kAI") == "Y" && c.EInvoiceEnabled; c.AutoEWayBill = Get("kAW") == "Y" && c.EWayBillEnabled;
            try
            {
                ConfigStore.Upsert(c);
                Log("eLog", "Saved. E-Invoice " + (c.EInvoiceEnabled ? "enabled" : "disabled (validate first)") + ", E-Way Bill " + (c.EWayBillEnabled ? "enabled" : "disabled (validate first)") + ".");
            }
            catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
        }
    }

    // =====================================================================================================
    // GST tax / unit / numbering mapping (SAP -> GST JSON)
    // =====================================================================================================
    public sealed class GstTaxMappingForm : FormBase
    {
        public const string Type = "EINV_GSTTAXMAP";

        public GstTaxMappingForm(AddonServices svc) : base(svc)
        {
            Create(Type, "GST Tax Mapping", 700, 520);
            int y = 14;
            Static("h", "SAP tax type (INV4) used for each GST component", 15, y, 600); y += 24;
            Static("a1", "CGST", 15, y, 170); Edit("e1", 190, y, 80); Static("a2", "SGST", 290, y, 40); Edit("e2", 335, y, 80); Static("a3", "IGST", 435, y, 40); Edit("e3", 480, y, 80); y += 22;
            Static("a4", "Cess (SAP type)", 15, y, 170); Edit("e4", 190, y, 80); y += 22;
            Static("a5", "Other charges (e.g. TCS types, comma)", 15, y, 170); Edit("e5", 190, y, 200); y += 22;
            Static("a6", "Freight SAC (if freight carries GST)", 15, y, 170); Edit("e6", 190, y, 120); y += 22;
            Static("a7", "Document number pattern", 15, y, 170); Edit("e7", 190, y, 200); Static("a7h", "{Prefix3} {SeriesName} {DocNum} {Branch:A=x}, max 16", 400, y, 280); y += 22;
            Static("a8", "Export port code", 15, y, 170); Edit("e8", 190, y, 100); Check("k9", "Exports are with payment of IGST (EXPWP)", 300, y, 320); y += 24;
            Static("a10", "Unit map (SAP=GST, ;-separated)", 15, y, 170); Edit("e10", 190, y, 480); y += 22;
            Static("a11", "State map (SAP=code, ;-separated)", 15, y, 170); Edit("e11", 190, y, 480); y += 28;
            Button("bUsed", "Show SAP tax types in use", 15, y, 180); Button("bUnits", "Show SAP units in use", 200, y, 170); Button("bSave", "Save", 375, y, 80); y += 28;
            MultiLine("eLog", 15, y, 660, 190);
            Load();
        }

        private static string Join(System.Collections.Generic.IDictionary<string, string> d) { return string.Join(";", d.Select(k => k.Key + "=" + k.Value)); }

        private static System.Collections.Generic.Dictionary<string, string> Parse(string s)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) { var kv = part.Split(new[] { '=' }, 2); if (kv.Length == 2 && kv[0].Trim().Length > 0 && kv[1].Trim().Length > 0) d[kv[0].Trim()] = kv[1].Trim(); }
            return d;
        }

        private void Load()
        {
            var c = Svc.Cfg;
            Func<string, string> typeOf = kind => string.Join(",", c.GstTaxTypeMap.Where(k => k.Value == kind).Select(k => k.Key));
            Set("e1", typeOf("cgst")); Set("e2", typeOf("sgst")); Set("e3", typeOf("igst")); Set("e4", typeOf("cess")); Set("e5", string.Join(",", c.OtherChargeTaxTypes));
            Set("e6", c.FreightSac); Set("e7", c.DocNumberPattern); Set("e8", c.ExportPortCode); Set("k9", c.ExportWithPayment ? "Y" : "N"); Set("e10", Join(c.UqcMap)); Set("e11", Join(c.StateCodeMap));
            Set("eLog", "A SAP tax with a non-zero amount that is mapped neither here nor as an 'other charge' stops the request with a clear message; nothing is dropped silently.");
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            if (!Authorization.IsAdmin(Svc)) { Log("eLog", "Only a configuration administrator can change the mapping."); return; }
            switch (e.ItemUID)
            {
                case "bUsed":
                    {
                        var rows = Svc.Di.Query("SELECT staType AS T, MAX(StaCode) AS C, COUNT(*) AS N FROM INV4 WHERE TaxSum<>0 GROUP BY staType ORDER BY COUNT(*) DESC");
                        Log("eLog", "SAP tax types with non-zero amounts: " + string.Join("; ", rows.Select(r => r["T"] + " (" + r["C"] + ", " + r["N"] + " lines)")));
                        break;
                    }
                case "bUnits":
                    {
                        var rows = Svc.Di.Query("SELECT TOP 40 unitMsr AS U, COUNT(*) AS N FROM INV1 WHERE ISNULL(unitMsr,'')<>'' GROUP BY unitMsr ORDER BY COUNT(*) DESC");
                        Log("eLog", "SAP units in use: " + string.Join("; ", rows.Select(r => r["U"] + " (" + r["N"] + ")")) + ". Map each to a GST unit code (KGS, NOS, PCS, LTR, MTR, ...).");
                        break;
                    }
                case "bSave": Save(); break;
            }
        }

        private void Save()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in new[] { new[] { "e1", "cgst" }, new[] { "e2", "sgst" }, new[] { "e3", "igst" }, new[] { "e4", "cess" } })
                foreach (var t in Get(pair[0]).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) map[t.Trim()] = pair[1];
            if (!map.Values.Contains("cgst") || !map.Values.Contains("sgst") || !map.Values.Contains("igst")) { Log("eLog", "CGST, SGST and IGST each need a SAP tax type."); return; }
            var c = Svc.Cfg;
            c.GstTaxTypeMap = map; c.OtherChargeTaxTypes = Get("e5").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            c.FreightSac = Get("e6"); c.DocNumberPattern = string.IsNullOrEmpty(Get("e7")) ? "{DocNum}" : Get("e7"); c.ExportPortCode = Get("e8"); c.ExportWithPayment = Get("k9") == "Y";
            c.UqcMap = Parse(Get("e10")); c.StateCodeMap = Parse(Get("e11"));
            try { ConfigStore.Upsert(c); Log("eLog", "Saved."); } catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
        }
    }
}
