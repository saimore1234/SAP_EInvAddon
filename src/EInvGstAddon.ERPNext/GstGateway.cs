using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    public sealed class GstIssue
    {
        public string Severity, Code, Field, Message;
    }

    /// <summary>Structured answer of the ERPNext GST API (the add-on never invents a success: Ok is exactly the server's ok).</summary>
    public sealed class GstApiResult
    {
        public bool Ok;
        public string Status = "", RequestId = "", Reference = "", Message = "";
        public JObject Data = new JObject();
        public List<GstIssue> Errors = new List<GstIssue>(), Warnings = new List<GstIssue>();

        public bool IsUnknown { get { return Status != null && Status.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase); } }
        public bool IsProcessing { get { return string.Equals(Status, "Processing", StringComparison.OrdinalIgnoreCase); } }
        public string S(string key) { var t = Data[key]; return t == null || t.Type == JTokenType.Null ? "" : (string)t; }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder(Message ?? "");
            foreach (var e in Errors) sb.Append("\n - ").Append(e.Message);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Direct client of the lightweight GST API of the ERPNext Frappe app (sap_b1_direct_integration.api.gst.*).
    /// It sends the SAP invoice as JSON; ERPNext validates, calls the GSP/IRP through the installed GST app and answers with the real result.
    /// No ERPNext Sales Invoice / Customer / Item is created or read by any method here.
    /// </summary>
    public sealed class GstGateway
    {
        public const string ApiModule = "sap_b1_direct_integration.api.gst.";
        private readonly ErpClient _erp;
        private readonly AppConfig _cfg;

        public GstGateway(ErpClient erp) { _erp = erp; _cfg = erp.Config; }

        // ------------------------------------------------------------------------------------------------ transport
        private JObject Envelope(int docEntry, JObject invoice = null, string objectType = "13")
        {
            var o = new JObject
            {
                ["request_id"] = Guid.NewGuid().ToString("N"), ["integration_id"] = _cfg.IntegrationId, ["sap_database"] = _cfg.SapDatabase, ["object_type"] = objectType,
                ["doc_entry"] = docEntry, ["environment"] = _cfg.Environment
            };
            if (invoice != null) o["invoice"] = invoice;
            return o;
        }

        private GstApiResult Call(string endpoint, JObject body)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = _erp.CallPost(ApiModule + endpoint, body);
            long ms = sw.ElapsedMilliseconds;
            var m = res == null ? null : res["message"] as JObject;
            if (m == null) throw new ErpApiException("The GST Portal GST API returned an unexpected response (is the sap_b1_direct_integration app installed?).", 0, null);
            var r = new GstApiResult
            {
                Ok = (bool?)m["ok"] ?? false, Status = (string)m["status"] ?? "", RequestId = (string)m["request_id"] ?? "", Reference = (string)m["reference"] ?? "", Message = (string)m["message"] ?? "",
                Data = m["data"] as JObject ?? new JObject()
            };
            Read(m["errors"], r.Errors); Read(m["warnings"], r.Warnings);
            Logger.Info("gst.api", "endpoint", endpoint, "status", r.Status, "ok", r.Ok, "ms", ms, "request", r.RequestId);
            return r;
        }

        private static void Read(JToken arr, List<GstIssue> into)
        {
            var a = arr as JArray;
            if (a == null) return;
            foreach (var x in a) into.Add(new GstIssue { Severity = (string)x["severity"], Code = (string)x["code"], Field = (string)x["field"], Message = (string)x["message"] });
        }

        private void Preflight()
        {
            try { _cfg.ValidateTarget(); } catch (InvalidOperationException ex) { throw new ConfigurationException(ex.Message); }
            if (string.IsNullOrWhiteSpace(_cfg.ErpUrl)) throw new ConfigurationException("The GST Portal URL is not configured.");
        }

        // ------------------------------------------------------------------------------------------------ operations
        public JObject Ping()
        {
            var r = _erp.Method("sap_b1_direct_integration.api.gst.ping", new Dictionary<string, string> { { "integration_id", _cfg.IntegrationId } });
            return r["message"] as JObject ?? new JObject();
        }

        public GstApiResult Validate(int docEntry, JObject invoice) { Preflight(); return Call("validate_invoice", Envelope(docEntry, invoice)); }
        public GstApiResult GenerateEInvoice(int docEntry, JObject invoice) { Preflight(); return Call("generate_e_invoice", Envelope(docEntry, invoice)); }

        /// <summary>Status of the e-Invoice transaction. When an invoice is supplied and the transaction is Unknown, the server reconciles it with the IRP.</summary>
        public GstApiResult EInvoiceStatus(int docEntry, JObject invoiceForReconciliation = null, bool refresh = true)
        {
            Preflight(); var b = Envelope(docEntry, invoiceForReconciliation); b["refresh"] = refresh; return Call("get_e_invoice_status", b);
        }

        public GstApiResult CancelEInvoice(int docEntry, string reason, string remark)
        {
            Preflight(); var b = Envelope(docEntry); b["reason"] = reason; b["remark"] = remark ?? ""; return Call("cancel_e_invoice", b);
        }

        public GstApiResult GenerateEWayBill(int docEntry, JObject invoice) { Preflight(); return Call("generate_e_waybill", Envelope(docEntry, invoice)); }

        public GstApiResult EWayBillStatus(int docEntry, JObject invoiceForReconciliation = null, bool refresh = true)
        {
            Preflight(); var b = Envelope(docEntry, invoiceForReconciliation); b["refresh"] = refresh; return Call("get_e_waybill_status", b);
        }

        public GstApiResult CancelEWayBill(int docEntry, string reason, string remark)
        {
            Preflight(); var b = Envelope(docEntry); b["reason"] = reason; b["remark"] = remark ?? ""; return Call("cancel_e_waybill", b);
        }

        public GstApiResult UpdateVehicle(int docEntry, string vehicle, string reason, string remark, string place, string stateCode, string mode = "Road", string vehicleType = "Regular")
        {
            Preflight();
            var b = Envelope(docEntry); b["vehicle_number"] = vehicle; b["reason"] = reason; b["remark"] = remark ?? ""; b["place"] = place; b["state_code"] = stateCode; b["transport_mode"] = mode; b["vehicle_type"] = vehicleType;
            return Call("update_vehicle", b);
        }

        public GstApiResult UpdateTransporter(int docEntry, string transporterGstin)
        {
            Preflight(); var b = Envelope(docEntry); b["transporter_id"] = transporterGstin; return Call("update_transporter", b);
        }

        // ------------------------------------------------------------------------------------------------ combined view for SAP
        /// <summary>
        /// Both statuses as one GstStatus (what the dialogs and the SAP records use). No ERPNext document exists, so DocStatus is always 1 ("ready").
        /// Never reports a document that the server did not report.
        /// </summary>
        public GstStatus Status(int docEntry, int docNum, bool refresh = true, JObject invoice = null)
        {
            var e = EInvoiceStatus(docEntry, invoice, refresh);
            var w = EWayBillStatus(docEntry, invoice, refresh);
            var s = new GstStatus { SalesInvoice = "SAP " + docNum, DocStatus = 1, Company = _cfg.ErpCompany };
            string es = e.Status;
            if (e.Ok && es == "Generated")
            {
                s.Irn = e.S("irn"); s.AckNo = e.S("ack_no"); s.AckDate = ParseDt(e.S("ack_date")); s.SignedQr = e.S("signed_qr"); s.SignedInvoice = e.S("signed_invoice"); s.EInvoiceStatus = "Generated";
            }
            else if (es == "Cancelled") { s.IrnCancelled = true; s.EInvoiceStatus = "Cancelled"; s.AckNo = e.S("ack_no"); s.AckDate = ParseDt(e.S("ack_date")); }
            else s.EInvoiceStatus = es == "Not generated" || string.IsNullOrEmpty(es) ? "Not generated" : es;
            s.EInvoiceUnknown = e.IsUnknown;
            if (w.Ok && w.Status == "Generated")
            {
                s.EwbNo = w.S("ewb_no"); s.EwbDate = ParseDt(w.S("ewb_date")); s.EwbValidUpto = ParseDt(w.S("ewb_valid_till")); s.VehicleNo = w.S("vehicle_no"); s.EwbStatus = "Generated";
            }
            else if (w.Status == "Cancelled") { s.EwbNo = w.S("ewb_no"); s.EwbCancelled = true; s.EwbStatus = "Cancelled"; }
            else s.EwbStatus = w.Status == "Not generated" || string.IsNullOrEmpty(w.Status) ? "Not generated" : w.Status;
            s.EwbUnknown = w.IsUnknown;
            s.LastMessage = (e.Message + " " + w.Message).Trim();
            return s;
        }

        public static DateTime? ParseDt(string s)
        {
            DateTime d;
            return !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null;
        }

        public GstRecord ToRecord(int docEntry, int docNum, GstStatus s, string customer = null, string gstin = null, decimal taxable = 0, decimal gst = 0, DateTime? invoiceDate = null)
        {
            string F(DateTime? d) { return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : ""; }
            return new GstRecord
            {
                DocEntry = docEntry, DocNum = docNum, ErpInvoice = "", Customer = customer, Gstin = gstin, InvoiceDate = invoiceDate, Taxable = taxable, Gst = gst,
                EInvStatus = s.HasIrn ? "Generated" : (s.IrnCancelled ? "Cancelled" : (s.EInvoiceUnknown ? "Unknown" : "Not generated")), Irn = s.Irn, AckNo = s.AckNo, AckDate = F(s.AckDate), SignedQr = s.SignedQr, SignedInvoice = s.SignedInvoice,
                CancelStatus = ((s.IrnCancelled ? "IRN cancelled " : "") + (s.EwbCancelled ? "EWB cancelled" : "")).Trim(),
                EwbStatus = s.EwbUnknown ? "Unknown" : s.EwbStatus, EwbNo = s.EwbNo, EwbDate = F(s.EwbDate), EwbValid = F(s.EwbValidUpto), Vehicle = s.VehicleNo, Mode = s.ModeOfTransport, Distance = s.Distance,
                Error = (s.EInvoiceUnknown || s.EwbUnknown) ? "UNKNOWN: " + s.LastMessage : null, LastSync = DateTime.Now
            };
        }
    }
}
