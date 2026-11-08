using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>Shared behaviour of the E-Invoice and E-Way Bill screens: pick a synchronised SAP invoice, run GST actions in the background, show the real result.</summary>
    public abstract class GstFormBase : FormBase
    {
        protected int DocEntry, DocNum;
        protected string ErpInvoice;
        protected GstStatus Status;

        protected GstFormBase(AddonServices svc) : base(svc) { }

        /// <summary>Resolve a SAP DocNum to the synchronised ERPNext invoice of THIS configuration.</summary>
        protected bool LoadInvoice(int docNum)
        {
            var r = Svc.Source.Query1("SELECT TOP 1 DocEntry FROM OINV WHERE DocNum=" + docNum + " AND CANCELED='N' ORDER BY DocEntry DESC");
            if (r == null) { Msg("SAP invoice with DocNum " + docNum + " was not found."); return false; }
            return LoadByDocEntry(Convert.ToInt32(r["DocEntry"], CultureInfo.InvariantCulture));
        }

        public bool LoadByDocEntry(int docEntry)
        {
            var rec = Svc.Store.GetRecord(docEntry);
            if (rec == null || rec.Status != SyncStatus.Synced || string.IsNullOrEmpty(rec.ErpInvoice))
            { Msg("DocEntry " + docEntry + " is not synchronised to ERPNext yet (status: " + (rec == null ? "none" : rec.Status) + "). Synchronise it first."); return false; }
            DocEntry = docEntry; DocNum = rec.DocNum; ErpInvoice = rec.ErpInvoice; Status = null;
            ShowIdentity();
            RefreshStatus();
            return true;
        }

        protected abstract void ShowIdentity();
        protected abstract void ShowStatus(GstStatus s);
        protected void Msg(string text) { try { Caption("lMsg", Trunc(text.Replace("\n", " | "), 250)); } catch { } try { Log("eLog", text.Replace("\n", " | ")); } catch { } }
        protected static string Trunc(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n); }
        protected static string Fmt(DateTime? d) { return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : ""; }
        protected static string Money(decimal d) { return d.ToString("N2", CultureInfo.InvariantCulture); }

        protected bool Require()
        {
            if (ErpInvoice == null) { Msg("Select a synchronised SAP invoice first (enter its DocNum and click Load)."); return false; }
            return true;
        }

        protected bool Confirm(string text)
        {
            try { return Svc.App.MessageBox(text, 2, "Yes", "No", "") == 1; } catch { return false; }
        }

        /// <summary>Reads the current ERPNext/GST state (read-only) and stores it in SAP.</summary>
        public void RefreshStatus()
        {
            if (!Require()) return;
            Msg("Reading ERPNext status ...");
            string inv = ErpInvoice; int de = DocEntry, dn = DocNum;
            Svc.Background("gst-status", () => { var g = Svc.NewGst(); return g.GetStatus(inv, true); }, s => { Status = s; ShowStatus(s); Persist(s); Msg("Status refreshed."); }, ex => Msg("Status failed: " + ex.Message));
        }

        protected void Persist(GstStatus s)
        {
            try { Svc.GstStore.SaveGst(Svc.NewGst().ToRecord(DocEntry, DocNum, s)); }
            catch (Exception ex) { Logger.Warn("gst.persist.failed", "error", ex.Message); }
        }

        protected void RunGst(string what, Func<GstService, GstResult> work)
        {
            if (!Require()) return;
            if (!Authorization.CanRunGst(Svc)) { Msg("Your SAP user is not authorised for GST actions in this configuration."); return; }
            Msg(what + " ...");
            Svc.Background(what, () => work(Svc.NewGst()), r =>
            {
                if (r.Status != null) { Status = r.Status; ShowStatus(r.Status); Persist(r.Status); }
                Msg((r.Success ? "OK: " : (r.Uncertain ? "UNCERTAIN: " : "NOT DONE: ")) + r.Message);
            }, ex => Msg(what + " failed: " + ex.Message));
        }

        protected void Validate(Func<GstService, GstStatus, GstCheck> check)
        {
            if (!Require()) return;
            Svc.Background("gst-validate", () =>
            {
                var g = Svc.NewGst(); var s = g.GetStatus(ErpInvoice, false);
                var c = check(g, s);
                var cfg = g.ValidateGstConfiguration();
                c.Errors.AddRange(cfg.Errors); c.Warnings.AddRange(cfg.Warnings);
                return c;
            }, c => Msg(c.Ok ? "Validation passed." + (c.Warnings.Count > 0 ? " Notes: " + string.Join("; ", c.Warnings) : "") : "Validation failed: " + string.Join("; ", c.Errors)), ex => Msg("Validation error: " + ex.Message));
        }

        protected void SaveBytes(string suggestedName, Func<byte[]> work, bool open)
        {
            if (!Require()) return;
            Msg("Downloading ...");
            Svc.Background("gst-download", work, bytes =>
            {
                string path;
                if (open) path = Path.Combine(Path.GetTempPath(), suggestedName);
                else
                {
                    using (var dlg = new SaveFileDialog { FileName = suggestedName, Filter = "All files|*.*" })
                    { if (dlg.ShowDialog() != DialogResult.OK) { Msg("Download cancelled."); return; } path = dlg.FileName; }
                }
                File.WriteAllBytes(path, bytes);
                if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Msg((open ? "Opened " : "Saved ") + path);
            }, ex => Msg("Download failed: " + ex.Message));
        }

        protected void SaveText(string suggestedName, string text)
        {
            if (string.IsNullOrEmpty(text)) { Msg("Nothing to save: ERPNext holds no such document for this invoice."); return; }
            using (var dlg = new SaveFileDialog { FileName = suggestedName, Filter = "Text/JSON|*.txt;*.json|All files|*.*" })
            { if (dlg.ShowDialog() != DialogResult.OK) return; File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false)); Msg("Saved " + dlg.FileName); }
        }
    }

    // =====================================================================================================
    // E-Invoice
    // =====================================================================================================
    public sealed class EInvoiceForm : GstFormBase
    {
        public const string Type = "EINV_EINVOICE";
        private static readonly string[] Fields = { "SAP Invoice Number", "ERPNext Invoice ID", "ERPNext Status", "Customer", "Customer GSTIN", "Supplier GSTIN", "Invoice Date", "Taxable Amount", "GST Amount", "Grand Total", "IRN Status", "IRN", "Acknowledgement No", "Acknowledgement Date" };

        public EInvoiceForm(AddonServices svc) : base(svc)
        {
            Create(Type, "Generate E-Invoice", 700, 560);
            Static("l0", "SAP DocNum", 15, 12, 90); Edit("eNum", 110, 10, 90); Button("bLoad", "Load", 210, 9, 70);
            for (int i = 0; i < Fields.Length; i++)
            {
                Static("k" + i, Fields[i], 15, 40 + i * 19, 150);
                Static("v" + i, "", 175, 40 + i * 19, 500);
            }
            int y = 40 + Fields.Length * 19 + 8;
            Button("bVal", "Validate Invoice", 15, y, 120); Button("bSub", "Submit in ERPNext", 140, y, 120); Button("bGen", "Generate E-Invoice", 265, y, 130); Button("bRef", "Refresh Status", 400, y, 110); y += 26;
            Button("bIrn", "View IRN", 15, y, 120); Button("bSig", "View Signed Invoice", 140, y, 120); Button("bQr", "View QR Code", 265, y, 130); Button("bDl", "Download Signed Invoice", 400, y, 150); y += 26;
            Button("bPrn", "Print E-Invoice", 15, y, 120); y += 28;
            Static("lR", "Cancel reason", 15, y + 2, 90); Combo("cR", 110, y, 150); Static("lK", "Remark", 275, y + 2, 50); Edit("eK", 330, y, 220); Button("bCan", "Cancel E-Invoice", 560, y - 2, 120);
            foreach (var r in GstService.CancelReasons) AddValid("cR", r, r);
            y += 28;
            Static("lMsg", "", 15, y, 660); y += 20;
            MultiLine("eLog", 15, y, 660, 70);
            Set("eLog", Svc.Cfg.Environment == "Production" ? "Environment: PRODUCTION - Generate / Cancel act on the real government portal." : "Environment: Sandbox.");
        }

        protected override void ShowIdentity() { Caption("v0", DocNum.ToString(CultureInfo.InvariantCulture)); Caption("v1", ErpInvoice); }

        protected override void ShowStatus(GstStatus s)
        {
            string[] v = { DocNum.ToString(CultureInfo.InvariantCulture), s.SalesInvoice, s.DocStatusText, s.CustomerName + " (" + s.Customer + ")", s.BillingGstin, s.CompanyGstin, s.PostingDate.HasValue ? s.PostingDate.Value.ToString("yyyy-MM-dd") : "",
                Money(s.NetTotal), Money(s.TaxTotal), Money(s.GrandTotal), s.HasIrn ? "Generated" : (s.IrnCancelled ? "Cancelled" : "Not generated"), s.Irn, s.AckNo, Fmt(s.AckDate) };
            for (int i = 0; i < v.Length; i++) Caption("v" + i, Trunc(v[i], 90));
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            int n;
            switch (e.ItemUID)
            {
                case "bLoad": if (int.TryParse(Get("eNum"), out n)) LoadInvoice(n); else Msg("Enter the SAP DocNum."); break;
                case "bRef": RefreshStatus(); break;
                case "bVal": Validate((g, s) => g.ValidateForEInvoice(s)); break;
                case "bSub":
                    if (Require() && Confirm("Submit Sales Invoice " + ErpInvoice + " in ERPNext?\nA submitted invoice is posted to the ERPNext books and can no longer be edited."))
                        RunGst("Submit in ERPNext", g => g.SubmitInvoice(ErpInvoice));
                    break;
                case "bGen":
                    if (Require() && Confirm("Generate the e-Invoice (IRN) for " + ErpInvoice + "?\nERPNext will call the " + Svc.Cfg.Environment + " IRP." + (Svc.Cfg.Environment == "Production" ? "\nThis creates a REAL IRN." : "")))
                        RunGst("Generate E-Invoice", g => g.GenerateEInvoice(ErpInvoice));
                    break;
                case "bIrn": if (Status != null) Svc.Alert("IRN: " + Status.Irn + "\nAck No: " + Status.AckNo + "\nAck Date: " + Fmt(Status.AckDate) + "\nStatus: " + (Status.HasIrn ? "Generated" : (Status.IrnCancelled ? "Cancelled" : "Not generated"))); break;
                case "bSig": if (Status != null) Svc.Alert(string.IsNullOrEmpty(Status.SignedInvoice) ? "No signed invoice is held by ERPNext for this document." : Trunc(Status.SignedInvoice, 1800)); break;
                case "bQr": if (Status != null && !string.IsNullOrEmpty(Status.SignedQr)) QrDialog.Show(Status); else Msg("No signed QR code is held by ERPNext for this invoice."); break;
                case "bDl": if (Status != null) SaveText("signed-einvoice-" + ErpInvoice.Replace('/', '-') + ".json", Status.SignedInvoice); break;
                case "bPrn": SaveBytes("einvoice-" + (ErpInvoice ?? "x").Replace('/', '-') + ".pdf", () => Svc.NewGst().DownloadInvoicePdf(ErpInvoice), true); break;
                case "bCan":
                    if (Require() && Confirm("Cancel the e-Invoice (IRN) of " + ErpInvoice + "?\nERPNext will also cancel the Sales Invoice and any active E-Way Bill. The SAP invoice is NOT changed."))
                        RunGst("Cancel E-Invoice", g => g.CancelEInvoice(ErpInvoice, Get("cR"), Get("eK")));
                    break;
            }
        }
    }

    // =====================================================================================================
    // E-Way Bill
    // =====================================================================================================
    public sealed class EWayBillForm : GstFormBase
    {
        public const string Type = "EINV_EWAYBILL";

        public EWayBillForm(AddonServices svc) : base(svc)
        {
            Create(Type, "Generate E-Way Bill", 720, 640);
            Static("l0", "SAP DocNum", 15, 12, 90); Edit("eNum", 110, 10, 90); Button("bLoad", "Load", 210, 9, 70);
            Static("k1", "ERPNext Invoice ID", 15, 38, 120); Static("v1", "", 140, 38, 250); Static("k2", "Customer", 400, 38, 60); Static("v2", "", 460, 38, 240);
            Static("k3", "Customer GSTIN", 15, 57, 120); Static("v3", "", 140, 57, 250);
            int y = 85;
            Static("a1", "Transporter ID (GSTIN)", 15, y, 140); Edit("eTid", 160, y, 160); Static("a2", "Transporter Name", 340, y, 110); Edit("eTn", 455, y, 220); y += 22;
            Static("a3", "Vehicle Number", 15, y, 140); Edit("eVeh", 160, y, 160); Static("a4", "Transport Mode", 340, y, 110); Combo("cMode", 455, y, 100);
            Static("a5", "Vehicle Type", 565, y, 70); Combo("cVt", 635, y, 70); y += 22;
            Static("a6", "Distance (km)", 15, y, 140); Edit("eDist", 160, y, 100); Static("a7", "Transport Doc No", 340, y, 110); Edit("eLr", 455, y, 100);
            Static("a8", "Doc Date (yyyy-mm-dd)", 565, y, 130); Edit("eLd", 635, y, 70); y += 26;
            foreach (var m in new[] { "Road", "Rail", "Air", "Ship" }) AddValid("cMode", m, m);
            AddValid("cVt", "Regular", "Regular"); AddValid("cVt", "Over Dimensional Cargo (ODC)", "ODC");
            Set("cMode", "Road"); Set("cVt", "Regular");
            Static("o1", "E-Way Bill No", 15, y, 140); Static("w1", "", 160, y, 160); Static("o2", "E-Way Bill Date", 340, y, 110); Static("w2", "", 455, y, 240); y += 19;
            Static("o3", "Valid Until", 15, y, 140); Static("w3", "", 160, y, 160); Static("o4", "Status", 340, y, 110); Static("w4", "", 455, y, 240); y += 26;
            Button("bVal", "Validate", 15, y, 90); Button("bSub", "Submit in ERPNext", 110, y, 120); Button("bGen", "Generate E-Way Bill", 235, y, 140); Button("bRef", "Refresh Status", 380, y, 110);
            Button("bDl", "Download E-Way Bill", 495, y, 140); y += 26;
            Button("bPrn", "Print E-Way Bill", 15, y, 120); y += 30;
            Static("u1", "Update reason", 15, y + 2, 90); Combo("cUr", 110, y, 150); Static("u2", "Place of change", 275, y + 2, 100); Edit("eUp", 380, y, 110); Static("u3", "State", 500, y + 2, 40); Edit("eUs", 545, y, 150); y += 24;
            foreach (var r in new[] { "Due to Break Down", "Due to Trans Shipment", "First Time", "Others" }) AddValid("cUr", r, r);
            Button("bVeh", "Update Vehicle Number", 15, y, 150); Button("bTr", "Update Transporter", 170, y, 140); y += 30;
            Static("c1", "Cancel reason", 15, y + 2, 90); Combo("cR", 110, y, 150); Static("c2", "Remark", 275, y + 2, 50); Edit("eK", 330, y, 220); Button("bCan", "Cancel E-Way Bill", 560, y - 2, 130);
            foreach (var r in GstService.CancelReasons) AddValid("cR", r, r);
            y += 28;
            Static("lMsg", "", 15, y, 680); y += 20;
            MultiLine("eLog", 15, y, 680, 60);
        }

        protected override void ShowIdentity() { Caption("v1", ErpInvoice); }

        protected override void ShowStatus(GstStatus s)
        {
            Caption("v1", s.SalesInvoice + " (" + s.DocStatusText + ")"); Caption("v2", Trunc(s.CustomerName, 40)); Caption("v3", s.BillingGstin);
            Caption("w1", s.EwbNo); Caption("w2", Fmt(s.EwbDate)); Caption("w3", Fmt(s.EwbValidUpto)); Caption("w4", s.EwbStatus);
            if (string.IsNullOrEmpty(Get("eVeh")) && !string.IsNullOrEmpty(s.VehicleNo)) Set("eVeh", s.VehicleNo);
            if (string.IsNullOrEmpty(Get("eTid")) && !string.IsNullOrEmpty(s.TransporterId)) Set("eTid", s.TransporterId);
            if (string.IsNullOrEmpty(Get("eTn")) && !string.IsNullOrEmpty(s.TransporterName)) Set("eTn", s.TransporterName);
            if (s.Distance > 0 && string.IsNullOrEmpty(Get("eDist"))) Set("eDist", s.Distance.ToString("0", CultureInfo.InvariantCulture));
        }

        private EWayBillInput Input()
        {
            decimal d; decimal.TryParse(Get("eDist"), NumberStyles.Any, CultureInfo.InvariantCulture, out d);
            return new EWayBillInput { TransporterId = Get("eTid").ToUpperInvariant(), TransporterName = Get("eTn"), VehicleNo = Get("eVeh"), ModeOfTransport = Get("cMode"), VehicleType = Get("cVt").StartsWith("Over") ? "Over Dimensional Cargo (ODC)" : "Regular", Distance = d, LrNo = Get("eLr"), LrDate = Get("eLd") };
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            int n;
            switch (e.ItemUID)
            {
                case "bLoad": if (int.TryParse(Get("eNum"), out n)) LoadInvoice(n); else Msg("Enter the SAP DocNum."); break;
                case "bRef": if (Require()) RunGst("Refresh from portal", g => Status != null && Status.HasEwb ? g.RefreshEWayBill(ErpInvoice) : new GstResult { Success = true, Message = "Status refreshed.", Status = g.GetStatus(ErpInvoice, true) }); break;
                case "bVal": { var t = Input(); Validate((g, s) => g.ValidateForEWayBill(s, t, true)); break; }
                case "bSub":
                    if (Require() && Confirm("Submit Sales Invoice " + ErpInvoice + " in ERPNext?\nA submitted invoice is posted to the ERPNext books and can no longer be edited."))
                        RunGst("Submit in ERPNext", g => g.SubmitInvoice(ErpInvoice));
                    break;
                case "bGen":
                    {
                        var t = Input();
                        if (Require() && Confirm("Generate the E-Way Bill for " + ErpInvoice + "?\nERPNext will call the " + Svc.Cfg.Environment + " e-Way Bill system." + (Svc.Cfg.Environment == "Production" ? "\nThis creates a REAL E-Way Bill." : "")))
                            RunGst("Generate E-Way Bill", g => g.GenerateEWayBill(ErpInvoice, t));
                        break;
                    }
                case "bVeh":
                    {
                        var t = Input(); string reason = Get("cUr"), place = Get("eUp"), state = Get("eUs"), remark = Get("eK");
                        if (string.IsNullOrEmpty(reason) || string.IsNullOrEmpty(place) || string.IsNullOrEmpty(state)) { Msg("Reason, place of change and state are required to update the vehicle."); break; }
                        if (Require() && Confirm("Update the vehicle number on the E-Way Bill of " + ErpInvoice + "?")) RunGst("Update vehicle", g => g.UpdateVehicle(ErpInvoice, t, reason, remark, place, state));
                        break;
                    }
                case "bTr":
                    {
                        string tid = Get("eTid").ToUpperInvariant();
                        if (Require() && Confirm("Update the transporter on the E-Way Bill of " + ErpInvoice + "?")) RunGst("Update transporter", g => g.UpdateTransporter(ErpInvoice, tid));
                        break;
                    }
                case "bDl": SaveBytes("eway-bill-" + (Status == null ? "x" : Status.EwbNo) + ".pdf", () => Svc.NewGst().DownloadEWayBillPdf(ErpInvoice), false); break;
                case "bPrn": SaveBytes("eway-bill-" + (Status == null ? "x" : Status.EwbNo) + ".pdf", () => Svc.NewGst().DownloadEWayBillPdf(ErpInvoice), true); break;
                case "bCan":
                    if (Require() && Confirm("Cancel the E-Way Bill of " + ErpInvoice + "?\nThis acts on the " + Svc.Cfg.Environment + " e-Way Bill system."))
                        RunGst("Cancel E-Way Bill", g => g.CancelEWayBill(ErpInvoice, Get("cR"), Get("eK")));
                    break;
            }
        }
    }

    /// <summary>Shows the actual signed QR code returned by the IRP (via ERPNext). Nothing is generated locally except the picture of the real payload.</summary>
    public static class QrDialog
    {
        public static void Show(GstStatus s)
        {
            UiThread.Post(() =>
            {
                using (var f = new Form { Text = "Signed QR Code - " + s.SalesInvoice, Width = 460, Height = 620, StartPosition = FormStartPosition.CenterScreen })
                {
                    var gen = new QRCoder.QRCodeGenerator();
                    var data = gen.CreateQrCode(s.SignedQr, QRCoder.QRCodeGenerator.ECCLevel.M);
                    var bmp = new QRCoder.QRCode(data).GetGraphic(5);
                    var pic = new PictureBox { Image = bmp, SizeMode = PictureBoxSizeMode.Zoom, Left = 10, Top = 10, Width = 420, Height = 420 };
                    var txt = new TextBox { Left = 10, Top = 440, Width = 420, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = "IRN: " + s.Irn + "\r\nAck No: " + s.AckNo + "\r\nAck Date: " + (s.AckDate.HasValue ? s.AckDate.Value.ToString("yyyy-MM-dd HH:mm") : "") + "\r\n\r\n" + s.SignedQr };
                    var save = new Button { Text = "Save PNG", Left = 10, Top = 548, Width = 100 };
                    save.Click += (a, b) => { using (var d = new SaveFileDialog { FileName = "qr-" + s.SalesInvoice.Replace('/', '-') + ".png", Filter = "PNG|*.png" }) if (d.ShowDialog() == DialogResult.OK) bmp.Save(d.FileName, System.Drawing.Imaging.ImageFormat.Png); };
                    f.Controls.AddRange(new Control[] { pic, txt, save });
                    f.ShowDialog();
                }
            }, "qr-dialog");
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
            foreach (var s in ewb ? new[] { "Not generated", "Generated", "Cancelled" } : new[] { "Not generated", "Generated", "Cancelled", "Failed" }) AddValid("cS", s, s);
            Button("bSearch", "Search", 700, 10, 70); Button("bRef", "Refresh Status", 775, 10, 100); Button("bExp", "Export to Excel", 880, 10, 100);
            Button("bOpen", ewb ? "Open E-Way Bill Screen" : "Open E-Invoice Screen", 15, 38, 170); Button("bSap", "View SAP Document", 190, 38, 150);
            QueryGrid("grid", "DT", 15, 66, 965, 400);
            Static("lMsg", "", 15, 475, 965);
            Search();
        }

        private void Search()
        {
            var w = new StringBuilder("WHERE s.U_Status='Synced' AND ISNULL(s.U_ConfigId,'')=" + SapReader.Lit(Svc.Cfg.StoredConfigId));
            string c = Get("eC").Replace("'", "''");
            if (c.Length > 0) w.Append(" AND (ISNULL(d.U_Customer,'') LIKE '%" + c + "%' OR ISNULL(i.CardName,'') LIKE '%" + c + "%' OR ISNULL(s.U_ErpInv,'') LIKE '%" + c + "%' OR CAST(s.U_DocNum AS VARCHAR(20)) LIKE '%" + c + "%')");
            string f = Get("eF"), t = Get("eT");
            if (f.Length == 8) w.Append(" AND i.DocDate>='" + f + "'");
            if (t.Length == 8) w.Append(" AND i.DocDate<='" + t + "'");
            string st = Get("cS");
            if (st.Length > 0) w.Append(" AND ISNULL(" + (_ewb ? "d.U_EwbStatus" : "d.U_EInvStatus") + ",'Not generated')='" + st.Replace("'", "''") + "'");
            string cols = _ewb
                ? "s.U_DocNum AS \"SAP DocNum\", s.U_ErpInv AS \"ERPNext Invoice\", d.U_EwbNo AS \"E-Way Bill No\", d.U_EwbDate AS \"E-Way Bill Date\", d.U_EwbValid AS \"Valid Until\", d.U_Transpr AS \"Transporter\", d.U_Vehicle AS \"Vehicle\", ISNULL(d.U_EwbStatus,'Not generated') AS \"Status\", s.U_DocEntry AS \"DocEntry\""
                : "s.U_DocNum AS \"SAP DocNum\", s.U_ErpInv AS \"ERPNext Invoice\", ISNULL(d.U_Customer,i.CardName) AS \"Customer\", d.U_Gstin AS \"GSTIN\", i.DocDate AS \"Invoice Date\", d.U_Taxable AS \"Taxable Amount\", d.U_GstAmt AS \"GST Amount\", d.U_Irn AS \"IRN\", d.U_AckNo AS \"Ack No\", d.U_AckDate AS \"Ack Date\", ISNULL(d.U_EInvStatus,'Not generated') AS \"Status\", s.U_DocEntry AS \"DocEntry\"";
            LoadGrid("grid", "DT", "SELECT " + cols + " FROM [@" + Constants.UdtSync + "] s LEFT JOIN [@" + Constants.UdtDocs + "] d ON d.U_DocEntry=s.U_DocEntry AND ISNULL(d.U_ConfigId,'')=ISNULL(s.U_ConfigId,'') LEFT JOIN OINV i ON i.DocEntry=s.U_DocEntry " + w + " ORDER BY s.U_DocEntry DESC");
        }

        private int? SelectedDocEntry()
        {
            var v = SelectedValue("grid", "DT", "DocEntry");
            int n; if (v == null || !int.TryParse(Convert.ToString(v), out n)) { Caption("lMsg", "Select a row first."); return null; }
            return n;
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
                    case "bSap": { var d = SelectedDocEntry(); if (d.HasValue) Svc.App.OpenForm(SAPbouiCOM.BoFormObjectEnum.fo_Invoice, "", d.Value.ToString(CultureInfo.InvariantCulture)); break; }
                    case "bOpen":
                        {
                            var d = SelectedDocEntry(); if (!d.HasValue) break;
                            var f = _ewb ? Svc.Router.Open(EWayBillForm.Type, () => new EWayBillForm(Svc)) : Svc.Router.Open(EInvoiceForm.Type, () => new EInvoiceForm(Svc));
                            ((GstFormBase)f).LoadByDocEntry(d.Value);
                            break;
                        }
                    case "bRef":
                        {
                            var d = SelectedDocEntry(); if (!d.HasValue) break;
                            var rec = Svc.Store.GetRecord(d.Value);
                            if (rec == null || string.IsNullOrEmpty(rec.ErpInvoice)) { Caption("lMsg", "Not synchronised."); break; }
                            Caption("lMsg", "Refreshing " + rec.ErpInvoice + " ...");
                            Svc.Background("register-refresh", () => Svc.NewGst().GetStatus(rec.ErpInvoice, true), s =>
                            {
                                Svc.GstStore.SaveGst(Svc.NewGst().ToRecord(rec.DocEntry, rec.DocNum, s)); Search(); Caption("lMsg", "Refreshed " + rec.ErpInvoice + ".");
                            }, ex => Caption("lMsg", "Refresh failed: " + ex.Message));
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
    // GST configuration
    // =====================================================================================================
    public sealed class GstConfigForm : FormBase
    {
        public const string Type = "EINV_GSTCFG";
        private static readonly string[] RegTypes = { "Regular", "Composition", "SEZ", "Other" };

        public GstConfigForm(AddonServices svc) : base(svc)
        {
            Create(Type, "GST Configuration", 640, 520);
            int y = 14;
            Static("a1", "Supplier GSTIN", 15, y, 150); Edit("eG", 175, y, 200); y += 22;
            Static("a2", "Legal Name", 15, y, 150); Edit("eL", 175, y, 420); y += 22;
            Static("a3", "Trade Name", 15, y, 150); Edit("eT", 175, y, 420); y += 22;
            Static("a4", "State Code (2 digits)", 15, y, 150); Edit("eS", 175, y, 60); y += 22;
            Static("a5", "GST Registration Type", 15, y, 150); Combo("cR", 175, y, 150); foreach (var r in RegTypes) AddValid("cR", r, r); y += 22;
            Static("a6", "Address", 15, y, 150); Edit("eA", 175, y, 420); y += 22;
            Static("a7", "PIN Code", 15, y, 150); Edit("eP", 175, y, 100); y += 22;
            Static("a8", "Default Place of Supply", 15, y, 150); Edit("ePos", 175, y, 200); y += 22;
            Static("a9", "Environment", 15, y, 150); Combo("cE", 175, y, 120); AddValid("cE", "Sandbox", "Sandbox"); AddValid("cE", "Production", "Production"); y += 22;
            Check("kI", "E-Invoice enabled", 175, y, 200); y += 20;
            Check("kW", "E-Way Bill enabled", 175, y, 200); y += 20;
            Check("kAI", "Automatic E-Invoice generation (after validation)", 175, y, 380); y += 20;
            Check("kAW", "Automatic E-Way Bill generation (after validation)", 175, y, 380); y += 24;
            Static("a10", "GSP / ASP status", 15, y, 150); Static("vS", "", 175, y, 420); y += 24;
            Button("bVal", "Validate against ERPNext", 15, y, 170); Button("bSave", "Save", 190, y, 90); y += 28;
            MultiLine("eLog", 15, y, 600, 110);
            Load();
        }

        private void Load()
        {
            var c = Svc.Cfg;
            Set("eG", c.SupplierGstin); Set("eL", c.LegalName); Set("eT", c.TradeName); Set("eS", c.StateCode); Set("cR", string.IsNullOrEmpty(c.GstRegistrationType) ? "Regular" : c.GstRegistrationType);
            Set("eA", c.GstAddress); Set("eP", c.GstPin); Set("ePos", c.DefaultPlaceOfSupply); Set("cE", c.Environment);
            Set("kI", c.EInvoiceEnabled ? "Y" : "N"); Set("kW", c.EWayBillEnabled ? "Y" : "N"); Set("kAI", c.AutoEInvoice ? "Y" : "N"); Set("kAW", c.AutoEWayBill ? "Y" : "N");
            Caption("vS", c.GspStatus);
            Set("eLog", "GST details are optional. Invoice synchronisation works without them; E-Invoice / E-Way Bill stay disabled until this configuration is validated.");
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
                        var tmp = Svc.Cfg.Clone(); tmp.SupplierGstin = Get("eG").ToUpperInvariant(); tmp.Environment = Get("cE"); tmp.EInvoiceEnabled = Get("kI") == "Y"; tmp.EWayBillEnabled = Get("kW") == "Y";
                        Log("eLog", "Validating against ERPNext ...");
                        Svc.Background("gst-validate-config", () => { using (var c = new ErpClient(tmp)) return new GstService(c).ValidateGstConfiguration(); }, r =>
                        {
                            Svc.Cfg.GspStatus = r.Ok ? "Configured" : "Not validated";
                            Caption("vS", Svc.Cfg.GspStatus);
                            Log("eLog", r.Ok ? "Validation OK." + (r.Warnings.Count > 0 ? " Notes: " + string.Join("; ", r.Warnings) : "") : "Validation FAILED: " + string.Join("; ", r.Errors));
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
            c.GstAddress = Get("eA"); c.GstPin = Get("eP"); c.DefaultPlaceOfSupply = Get("ePos"); c.Environment = string.IsNullOrEmpty(Get("cE")) ? "Production" : Get("cE");
            c.EInvoiceEnabled = Get("kI") == "Y" && c.GspStatus == "Configured"; c.EWayBillEnabled = Get("kW") == "Y" && c.GspStatus == "Configured";
            c.AutoEInvoice = Get("kAI") == "Y" && c.EInvoiceEnabled; c.AutoEWayBill = Get("kAW") == "Y" && c.EWayBillEnabled;
            try
            {
                ConfigStore.Upsert(c); Svc.ResetEngine();
                Log("eLog", "Saved. E-Invoice " + (c.EInvoiceEnabled ? "enabled" : "disabled (validate first)") + ", E-Way Bill " + (c.EWayBillEnabled ? "enabled" : "disabled (validate first)") + ".");
            }
            catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
        }
    }

    // =====================================================================================================
    // Tax & account mapping
    // =====================================================================================================
    public sealed class TaxMappingForm : FormBase
    {
        public const string Type = "EINV_TAXMAP";

        public TaxMappingForm(AddonServices svc) : base(svc)
        {
            Create(Type, "Tax & Account Mapping", 700, 500);
            int y = 14;
            Static("h", "SAP tax type  ->  ERPNext account (of company " + svc.Cfg.ErpCompany + ")", 15, y, 600); y += 24;
            Static("a1", "CGST (-100)", 15, y, 150); Edit("e100", 175, y, 350); y += 22;
            Static("a2", "SGST (-110)", 15, y, 150); Edit("e110", 175, y, 350); y += 22;
            Static("a3", "IGST (-120)", 15, y, 150); Edit("e120", 175, y, 350); y += 22;
            Static("a4", "Cess (SAP type, e.g. -90)", 15, y, 150); Edit("eCt", 175, y, 60); Edit("eCa", 240, y, 285); y += 22;
            Static("a5", "Other SAP type (TCS etc.)", 15, y, 150); Edit("eOt", 175, y, 60); Edit("eOa", 240, y, 285); y += 26;
            Static("a6", "Freight account", 15, y, 150); Edit("eFr", 175, y, 350); y += 22;
            Static("a7", "Round-off account", 15, y, 150); Edit("eRo", 175, y, 350); y += 22;
            Static("a8", "Default income account", 15, y, 150); Edit("eIn", 175, y, 350); y += 22;
            Static("a9", "Default cost centre", 15, y, 150); Edit("eCc", 175, y, 350); y += 28;
            Button("bFetch", "Fetch from ERPNext GST Settings", 15, y, 200); Button("bUsed", "Show SAP tax types in use", 220, y, 180); Button("bSave", "Save", 405, y, 80); y += 28;
            MultiLine("eLog", 15, y, 660, 150);
            Load();
        }

        private void Load()
        {
            var m = Svc.Cfg.TaxAccountMap; string v;
            Set("e100", m.TryGetValue("-100", out v) ? v : ""); Set("e110", m.TryGetValue("-110", out v) ? v : ""); Set("e120", m.TryGetValue("-120", out v) ? v : "");
            var other = m.Where(k => k.Key != "-100" && k.Key != "-110" && k.Key != "-120").ToList();
            if (other.Count > 0) { Set("eCt", other[0].Key); Set("eCa", other[0].Value); }
            if (other.Count > 1) { Set("eOt", other[1].Key); Set("eOa", other[1].Value); }
            Set("eFr", Svc.Cfg.FreightAccount); Set("eRo", Svc.Cfg.RoundOffAccount); Set("eIn", Svc.Cfg.DefaultIncomeAccount); Set("eCc", Svc.Cfg.DefaultCostCenter);
            Set("eLog", "Unmapped SAP taxes with a non-zero amount stop the synchronisation of that invoice (nothing is created with a wrong tax). Accounts must belong to company " + Svc.Cfg.ErpCompany + ".");
        }

        public override void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction || e.EventType != SAPbouiCOM.BoEventTypes.et_ITEM_PRESSED) return;
            if (!Authorization.IsAdmin(Svc)) { Log("eLog", "Only a configuration administrator can change the mapping."); return; }
            switch (e.ItemUID)
            {
                case "bFetch":
                    Log("eLog", "Reading the GST accounts configured in ERPNext for this company ...");
                    Svc.Background("fetch-gst-accounts", () => { string cess; var m = Svc.NewGst().SuggestGstAccounts(out cess); return Tuple.Create(m, cess); }, r =>
                    {
                        string v;
                        if (r.Item1.TryGetValue("-100", out v)) Set("e100", v); if (r.Item1.TryGetValue("-110", out v)) Set("e110", v); if (r.Item1.TryGetValue("-120", out v)) Set("e120", v);
                        Log("eLog", r.Item1.Count == 0 ? "No Output GST accounts are configured for this company in ERPNext GST Settings." : "Filled CGST/SGST/IGST from ERPNext GST Settings. Cess account in ERPNext: " + (r.Item2 ?? "(none)") + ". Review, then Save.");
                    }, ex => Log("eLog", "Failed: " + ex.Message));
                    break;
                case "bUsed":
                    {
                        var rows = Svc.Di.Query("SELECT staType AS T, MAX(StaCode) AS C, COUNT(*) AS N, SUM(TaxSum) AS S FROM INV4 WHERE TaxSum<>0 GROUP BY staType ORDER BY COUNT(*) DESC");
                        Log("eLog", "SAP tax types with non-zero amounts: " + string.Join("; ", rows.Select(r => r["T"] + " (" + r["C"] + ", " + r["N"] + " lines)")));
                        break;
                    }
                case "bSave": Save(); break;
            }
        }

        private void Save()
        {
            var m = new Dictionary<string, string>();
            Action<string, string> add = (k, a) => { if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(a)) m[k.Trim()] = a.Trim(); };
            add("-100", Get("e100")); add("-110", Get("e110")); add("-120", Get("e120")); add(Get("eCt"), Get("eCa")); add(Get("eOt"), Get("eOa"));
            Svc.Background("validate-accounts", () =>
            {
                var c = Svc.NewClient(); var lk = new ErpLookups(c); var bad = new List<string>();
                var all = m.Values.Concat(new[] { Get("eFr"), Get("eRo"), Get("eIn") }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct();
                foreach (var a in all) { string owner = lk.AccountCompany(a); if (owner == null) bad.Add(a + " (does not exist)"); else if (!string.Equals(owner, Svc.Cfg.ErpCompany, StringComparison.OrdinalIgnoreCase)) bad.Add(a + " (belongs to " + owner + ")"); }
                return bad;
            }, bad =>
            {
                if (bad.Count > 0) { Log("eLog", "Not saved - invalid accounts: " + string.Join("; ", bad)); return; }
                var c = Svc.Cfg; c.TaxAccountMap = m; c.FreightAccount = Get("eFr"); c.RoundOffAccount = Get("eRo"); c.DefaultIncomeAccount = Get("eIn"); c.DefaultCostCenter = Get("eCc");
                try { ConfigStore.Upsert(c); Svc.ResetEngine(); Log("eLog", "Saved " + m.Count + " tax mapping(s)."); } catch (Exception ex) { Log("eLog", "Save failed: " + ex.Message); }
            }, ex => Log("eLog", "Validation failed: " + ex.Message));
        }
    }
}
