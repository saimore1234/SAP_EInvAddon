using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    public sealed class GstResult
    {
        public bool Success;
        public string Message;
        public GstStatus Status;
        public bool Uncertain;     // the outcome could not be confirmed (timeout); status was re-read, nothing was retried
    }

    /// <summary>
    /// E-Invoice / E-Way Bill operations. The add-on contains NO GSP/IRP logic: it calls the whitelisted methods of the GST app installed in ERPNext
    /// (india_compliance, signatures verified in v15.29.0) and reports what ERPNext/IRP actually returned. An IRN or e-Way Bill number is only ever
    /// taken from ERPNext after a successful call; nothing is generated, guessed or simulated here.
    /// </summary>
    public sealed class GstService
    {
        // Verified in india_compliance 15.29.0 (see docs/COMPATIBILITY.md)
        public const string EInvoiceModule = "india_compliance.gst_india.utils.e_invoice.";
        public const string EWaybillModule = "india_compliance.gst_india.utils.e_waybill.";
        public const string VerifiedGstApp = "15.29.0";
        public static readonly string[] CancelReasons = { "Duplicate", "Order Cancelled", "Data Entry Mistake", "Others" };

        private readonly ErpClient _erp;
        private readonly AppConfig _cfg;
        public GstService(ErpClient erp) { _erp = erp; _cfg = erp.Config; }

        // =================================================================== compatibility / configuration
        public sealed class Versions { public string Frappe, Erpnext, IndiaCompliance; public bool Compatible; public string Note; }

        public Versions CheckVersions()
        {
            var r = _erp.Method("frappe.utils.change_log.get_versions");
            var m = (JObject)r["message"];
            Func<string, string> v = app => m[app] == null ? null : (string)m[app]["version"];
            var res = new Versions { Frappe = v("frappe"), Erpnext = v("erpnext"), IndiaCompliance = v("india_compliance") };
            if (res.IndiaCompliance == null) { res.Note = "The India Compliance GST app is not installed on this GST Portal site; E-Invoice / E-Way Bill are unavailable (invoice synchronisation is not affected)."; return res; }
            bool major15 = res.Frappe != null && res.Frappe.StartsWith("15.") && res.IndiaCompliance.StartsWith("15.");
            res.Compatible = major15;
            res.Note = !major15
                ? "Unsupported versions (Frappe " + res.Frappe + ", India Compliance " + res.IndiaCompliance + "). The GST calls were verified only for Frappe 15 / India Compliance 15.x. GST actions are disabled."
                : (res.IndiaCompliance == VerifiedGstApp ? "India Compliance " + res.IndiaCompliance + " (verified version)." : "India Compliance " + res.IndiaCompliance + " is a different 15.x release than the verified " + VerifiedGstApp + "; same API expected, test in sandbox first.");
            return res;
        }

        /// <summary>Checks the ERPNext GST Settings for this company. Used by the GST screen and before any GST action.</summary>
        public GstCheck ValidateGstConfiguration()
        {
            var c = new GstCheck();
            var ver = CheckVersions();
            if (!ver.Compatible) { c.Errors.Add(ver.Note); return c; }
            if (ver.Note.Contains("different")) c.Warnings.Add(ver.Note);

            var gs = _erp.GetDoc("GST Settings", "GST Settings");
            if (gs == null) { c.Errors.Add("GST Settings not found in GST Portal."); return c; }
            if (!Truthy(gs["enable_api"])) c.Errors.Add("GST API is not enabled in GST Portal GST Settings (enable_api).");
            var co = _erp.GetDoc("Company", _cfg.ErpCompany);
            if (co == null) { c.Errors.Add("GST Portal company '" + _cfg.ErpCompany + "' not found."); return c; }
            string gstin = (string)co["gstin"];
            if (string.IsNullOrWhiteSpace(gstin)) c.Errors.Add("The GST Portal company has no GSTIN. Set it on the company (via its GST address) before using E-Invoice / E-Way Bill.");
            else if (!string.IsNullOrWhiteSpace(_cfg.SupplierGstin) && !string.Equals(gstin, _cfg.SupplierGstin.Trim(), StringComparison.OrdinalIgnoreCase))
                c.Errors.Add("Supplier GSTIN in this configuration (" + _cfg.SupplierGstin + ") differs from the GSTIN of the GST Portal company (" + gstin + ").");

            // credentials per company
            var creds = gs["credentials"] as JArray ?? new JArray();
            bool hasCred = creds.Any(x => string.Equals((string)x["company"], _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase) && ((string)x["service"] ?? "").IndexOf("Invoice", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasCred) c.Errors.Add("No GSP credentials for e-Waybill / e-Invoice are configured in GST Portal GST Settings for company '" + _cfg.ErpCompany + "'.");

            // sandbox/production consistency (GST Settings sandbox_mode is global on the site)
            bool siteSandbox = Truthy(gs["sandbox_mode"]);
            bool cfgSandbox = string.Equals(_cfg.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase);
            if (cfgSandbox && !siteSandbox) c.Errors.Add("This configuration is marked Sandbox but GST Portal GST Settings is in PRODUCTION mode: a generated IRN would be real. Blocked.");
            if (!cfgSandbox && siteSandbox) c.Warnings.Add("This configuration is marked Production but GST Portal GST Settings is in sandbox mode: documents will not be real.");
            if (!Truthy(gs["enable_e_invoice"])) c.Warnings.Add("e-Invoice is not enabled in GST Portal GST Settings.");
            if (!Truthy(gs["enable_e_waybill"])) c.Warnings.Add("e-Waybill is not enabled in GST Portal GST Settings.");
            return c;
        }

        private static bool Truthy(JToken t) { return t != null && t.Type != JTokenType.Null && (t.Type == JTokenType.Boolean ? (bool)t : Convert.ToInt32(t.ToString(), CultureInfo.InvariantCulture) != 0); }
        private static DateTime? Dt(JToken t) { DateTime d; return t != null && t.Type != JTokenType.Null && DateTime.TryParse((string)t, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null; }
        private static decimal Dec(JToken t) { return t == null || t.Type == JTokenType.Null ? 0m : Convert.ToDecimal(t.ToString(), CultureInfo.InvariantCulture); }
        private static string Str(JToken t) { return t == null || t.Type == JTokenType.Null ? "" : (string)t; }

        // =================================================================== status
        public GstStatus GetStatus(string salesInvoice, bool includeDocuments = false)
        {
            var d = _erp.GetDoc("Sales Invoice", salesInvoice);
            if (d == null) throw new ConfigurationException("Sales Invoice '" + salesInvoice + "' does not exist in GST Portal.");
            string company = Str(d["company"]);
            if (!string.Equals(company, _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Safety stop: Sales Invoice '" + salesInvoice + "' belongs to company '" + company + "', not '" + _cfg.ErpCompany + "'.");
            var s = new GstStatus
            {
                SalesInvoice = salesInvoice, Company = company, Customer = Str(d["customer"]), CustomerName = Str(d["customer_name"]),
                CompanyGstin = Str(d["company_gstin"]), BillingGstin = Str(d["billing_address_gstin"]), PlaceOfSupply = Str(d["place_of_supply"]),
                DocStatus = d["docstatus"] == null ? 0 : (int)d["docstatus"], PostingDate = Dt(d["posting_date"]),
                NetTotal = Dec(d["net_total"]), TaxTotal = Dec(d["total_taxes_and_charges"]), GrandTotal = Dec(d["rounded_total"]) != 0 ? Dec(d["rounded_total"]) : Dec(d["grand_total"]),
                Irn = Str(d["irn"]), EInvoiceStatus = Str(d["einvoice_status"]), EwbNo = Str(d["ewaybill"]),
                VehicleNo = Str(d["vehicle_no"]), TransporterId = Str(d["gst_transporter_id"]), TransporterName = Str(d["transporter_name"]),
                ModeOfTransport = Str(d["mode_of_transport"]), VehicleType = Str(d["gst_vehicle_type"]), LrNo = Str(d["lr_no"]), LrDate = Str(d["lr_date"]), Distance = Dec(d["distance"])
            };

            // acknowledgement details live in the e-Invoice Log (latest entry of this document)
            string[] f = includeDocuments
                ? new[] { "name", "acknowledgement_number", "acknowledged_on", "signed_qr_code", "signed_invoice", "is_cancelled", "cancelled_on" }
                : new[] { "name", "acknowledgement_number", "acknowledged_on", "signed_qr_code", "is_cancelled", "cancelled_on" };
            var logs = _erp.GetList("e-Invoice Log", f, new object[] { new object[] { "reference_doctype", "=", "Sales Invoice" }, new object[] { "reference_name", "=", salesInvoice } }, 5, "creation desc");
            if (logs.Count > 0)
            {
                var l = (JObject)logs[0];
                s.AckNo = Str(l["acknowledgement_number"]); s.AckDate = Dt(l["acknowledged_on"]); s.SignedQr = Str(l["signed_qr_code"]);
                if (includeDocuments) s.SignedInvoice = Str(l["signed_invoice"]);
                s.IrnCancelled = Truthy(l["is_cancelled"]); s.IrnCancelledOn = Dt(l["cancelled_on"]);
                if (!s.HasIrn && s.IrnCancelled) s.Irn = "";
            }

            var wl = _erp.GetList("e-Waybill Log", new[] { "name", "e_waybill_number", "valid_upto", "created_on", "is_cancelled" },
                new object[] { new object[] { "reference_doctype", "=", "Sales Invoice" }, new object[] { "reference_name", "=", salesInvoice } }, 5, "creation desc");
            if (wl.Count > 0)
            {
                var w = (JObject)wl[0];
                if (string.IsNullOrEmpty(s.EwbNo)) s.EwbNo = Str(w["e_waybill_number"]);
                s.EwbDate = Dt(w["created_on"]); s.EwbValidUpto = Dt(w["valid_upto"]); s.EwbCancelled = Truthy(w["is_cancelled"]);
            }
            s.EwbStatus = string.IsNullOrEmpty(s.EwbNo) ? "Not generated" : (s.EwbCancelled ? "Cancelled" : "Generated");
            return s;
        }

        // =================================================================== pre-checks
        public GstCheck ValidateForEInvoice(GstStatus s)
        {
            var c = new GstCheck();
            if (!_cfg.EInvoiceEnabled) c.Errors.Add("E-Invoice is not enabled in this configuration's GST screen.");
            if (s.DocStatus == 0) c.Errors.Add("The GST Portal Sales Invoice is a Draft. Submit it in GST Portal first (use 'Submit in GST Portal' after checking it).");
            if (s.DocStatus == 2) c.Errors.Add("The GST Portal Sales Invoice is cancelled.");
            if (s.HasIrn) c.Errors.Add("An IRN already exists for this invoice.");
            if (string.IsNullOrWhiteSpace(s.CompanyGstin)) c.Errors.Add("The invoice has no company GSTIN (supplier GSTIN missing in GST Portal).");
            if (string.IsNullOrWhiteSpace(s.BillingGstin) && s.PlaceOfSupply != "96-Other Countries") c.Errors.Add("Customer billing GSTIN missing: e-Invoice is not applicable to B2C invoices.");
            if (!string.IsNullOrWhiteSpace(s.CompanyGstin) && s.CompanyGstin == s.BillingGstin) c.Errors.Add("Company and billing GSTIN are the same.");
            return c;
        }

        public GstCheck ValidateForEWayBill(GstStatus s, EWayBillInput t, bool forGeneration)
        {
            var c = new GstCheck();
            if (!_cfg.EWayBillEnabled) c.Errors.Add("E-Way Bill is not enabled in this configuration's GST screen.");
            if (s.DocStatus != 1) c.Errors.Add("The GST Portal Sales Invoice must be Submitted before an E-Way Bill can be generated.");
            if (forGeneration && s.HasEwb) c.Errors.Add("An E-Way Bill already exists (" + s.EwbNo + ").");
            if (t != null)
            {
                string[] modes = { "Road", "Rail", "Air", "Ship" };
                if (!modes.Contains(t.ModeOfTransport ?? "")) c.Errors.Add("Transport mode must be Road, Rail, Air or Ship.");
                if (t.Distance < 0 || t.Distance > 4000) c.Errors.Add("Distance must be between 0 (auto) and 4000 km.");
                if ((t.ModeOfTransport ?? "Road") == "Road" && string.IsNullOrWhiteSpace(t.VehicleNo) && string.IsNullOrWhiteSpace(t.TransporterId))
                    c.Errors.Add("For road transport enter a vehicle number or a transporter GSTIN.");
                if (!string.IsNullOrWhiteSpace(t.VehicleNo) && !System.Text.RegularExpressions.Regex.IsMatch(t.VehicleNo.Replace(" ", "").ToUpperInvariant(), @"^[A-Z]{2}[0-9]{1,2}[A-Z]{0,3}[0-9]{4}$|^[0-9]{2}BH[0-9]{4}[A-Z]{1,2}$|^TM[0-9]{6}[A-Z]?$"))
                    c.Errors.Add("Vehicle number '" + t.VehicleNo + "' is not a valid format (e.g. MH04AB1234).");
                if (!string.IsNullOrWhiteSpace(t.TransporterId) && !System.Text.RegularExpressions.Regex.IsMatch(t.TransporterId.ToUpperInvariant(), @"^[0-9]{2}[A-Z0-9]{10}[0-9A-Z]{3}$"))
                    c.Errors.Add("Transporter ID must be a valid 15-character GSTIN.");
                if (!string.IsNullOrWhiteSpace(t.LrNo) && string.IsNullOrWhiteSpace(t.LrDate) && t.ModeOfTransport != "Road") c.Errors.Add("Transport document date is required with the document number.");
            }
            if (forGeneration && s.GrandTotal < 50000m) c.Warnings.Add("Invoice value is below the usual e-Waybill threshold; GST Portal applies the threshold in GST Settings.");
            return c;
        }

        public GstCheck ValidateEInvoiceCancel(GstStatus s, string reason)
        {
            var c = new GstCheck();
            if (!s.HasIrn) c.Errors.Add("There is no active IRN to cancel.");
            if (!CancelReasons.Contains(reason ?? "")) c.Errors.Add("Select a cancellation reason.");
            if (s.AckDate.HasValue && s.AckDate.Value.AddDays(1) < DateTime.Now) c.Errors.Add("An e-Invoice can be cancelled only within 24 hours of generation.");
            if (!s.AckDate.HasValue && s.HasIrn) c.Errors.Add("The acknowledgement time is unknown; refresh the status first.");
            c.Warnings.Add("GST Portal also cancels the Sales Invoice (and its active E-Way Bill) when the IRN is cancelled. The SAP invoice is never changed.");
            return c;
        }

        public GstCheck ValidateEWayBillCancel(GstStatus s, string reason)
        {
            var c = new GstCheck();
            if (!s.HasEwb) c.Errors.Add("There is no active E-Way Bill to cancel.");
            if (!CancelReasons.Contains(reason ?? "")) c.Errors.Add("Select a cancellation reason.");
            if (s.EwbDate.HasValue && s.EwbDate.Value.AddDays(1) < DateTime.Now) c.Errors.Add("An E-Way Bill can be cancelled only within 24 hours of generation.");
            return c;
        }

        // =================================================================== actions
        /// <summary>Explicit, user-triggered submission of the ERPNext Draft (never automatic).</summary>
        public GstResult SubmitInvoice(string salesInvoice)
        {
            var s = GetStatus(salesInvoice);
            if (s.DocStatus == 1) return new GstResult { Success = true, Message = "Already submitted.", Status = s };
            if (s.DocStatus == 2) return new GstResult { Success = false, Message = "The invoice is cancelled.", Status = s };
            try { _erp.Update("Sales Invoice", salesInvoice, new JObject { ["docstatus"] = 1 }); }
            catch (ErpApiException ex) { return new GstResult { Success = false, Message = "GST Portal refused the submission: " + ex.Message, Status = s }; }
            var after = GetStatus(salesInvoice);
            return new GstResult { Success = after.DocStatus == 1, Message = after.DocStatus == 1 ? "Submitted in GST Portal." : "Submission not confirmed by GST Portal.", Status = after };
        }

        public GstResult GenerateEInvoice(string salesInvoice)
        {
            var before = GetStatus(salesInvoice);
            var chk = ValidateForEInvoice(before);
            if (!chk.Ok) return new GstResult { Success = false, Message = chk.ToString(), Status = before };
            var cfgChk = ValidateGstConfiguration();
            if (!cfgChk.Ok) return new GstResult { Success = false, Message = cfgChk.ToString(), Status = before };
            return Call(EInvoiceModule + "generate_e_invoice", new JObject { ["docname"] = salesInvoice, ["throw"] = true, ["force"] = false }, salesInvoice,
                s => s.HasIrn, "e-Invoice generated by the IRP (IRN received).", "GST Portal did not return an IRN.");
        }

        public GstResult CancelEInvoice(string salesInvoice, string reason, string remark)
        {
            var before = GetStatus(salesInvoice);
            var chk = ValidateEInvoiceCancel(before, reason);
            if (!chk.Ok) return new GstResult { Success = false, Message = chk.ToString(), Status = before };
            return Call(EInvoiceModule + "cancel_e_invoice",
                new JObject { ["docname"] = salesInvoice, ["values"] = new JObject { ["reason"] = reason, ["remark"] = string.IsNullOrWhiteSpace(remark) ? reason : remark } }, salesInvoice,
                s => !s.HasIrn, "e-Invoice cancelled (confirmed by GST Portal).", "GST Portal still shows an active IRN.");
        }

        public GstResult GenerateEWayBill(string salesInvoice, EWayBillInput t)
        {
            var before = GetStatus(salesInvoice);
            var chk = ValidateForEWayBill(before, t, true);
            if (!chk.Ok) return new GstResult { Success = false, Message = chk.ToString(), Status = before };
            var cfgChk = ValidateGstConfiguration();
            if (!cfgChk.Ok) return new GstResult { Success = false, Message = cfgChk.ToString(), Status = before };
            return Call(EWaybillModule + "generate_e_waybill",
                new JObject { ["doctype"] = "Sales Invoice", ["docname"] = salesInvoice, ["values"] = TransportValues(t), ["force"] = false }, salesInvoice,
                s => s.HasEwb, "E-Way Bill generated (number received).", "GST Portal did not return an E-Way Bill number.");
        }

        public GstResult CancelEWayBill(string salesInvoice, string reason, string remark)
        {
            var before = GetStatus(salesInvoice);
            var chk = ValidateEWayBillCancel(before, reason);
            if (!chk.Ok) return new GstResult { Success = false, Message = chk.ToString(), Status = before };
            return Call(EWaybillModule + "cancel_e_waybill",
                new JObject { ["doctype"] = "Sales Invoice", ["docname"] = salesInvoice, ["values"] = new JObject { ["reason"] = reason, ["remark"] = string.IsNullOrWhiteSpace(remark) ? reason : remark } }, salesInvoice,
                s => !s.HasEwb, "E-Way Bill cancelled (confirmed by GST Portal).", "GST Portal still shows an active E-Way Bill.");
        }

        public GstResult UpdateVehicle(string salesInvoice, EWayBillInput t, string reason, string remark, string placeOfChange, string stateName)
        {
            var before = GetStatus(salesInvoice);
            var chk = ValidateForEWayBill(before, t, false);
            if (!before.HasEwb) chk.Errors.Add("There is no active E-Way Bill.");
            if (string.IsNullOrWhiteSpace(t.VehicleNo)) chk.Errors.Add("Enter the new vehicle number.");
            if (!chk.Ok) return new GstResult { Success = false, Message = chk.ToString(), Status = before };
            var v = TransportValues(t);
            v["reason"] = reason; v["remark"] = remark ?? reason; v["place_of_change"] = placeOfChange; v["state"] = stateName; v["update_e_waybill_data"] = true;
            string vehicleBefore = before.VehicleNo;
            return Call(EWaybillModule + "update_vehicle_info", new JObject { ["doctype"] = "Sales Invoice", ["docname"] = salesInvoice, ["values"] = v }, salesInvoice,
                s => s.VehicleNo.Replace(" ", "") == t.VehicleNo.Replace(" ", ""), "Vehicle updated on the E-Way Bill.", "GST Portal did not confirm the vehicle change.");
        }

        public GstResult UpdateTransporter(string salesInvoice, string transporterGstin)
        {
            var before = GetStatus(salesInvoice);
            var chk = ValidateForEWayBill(before, new EWayBillInput { TransporterId = transporterGstin }, false);
            if (!before.HasEwb) chk.Errors.Add("There is no active E-Way Bill.");
            if (string.IsNullOrWhiteSpace(transporterGstin)) chk.Errors.Add("Enter the transporter GSTIN.");
            if (!chk.Ok) return new GstResult { Success = false, Message = chk.ToString(), Status = before };
            return Call(EWaybillModule + "update_transporter",
                new JObject { ["doctype"] = "Sales Invoice", ["docname"] = salesInvoice, ["values"] = new JObject { ["gst_transporter_id"] = transporterGstin, ["update_e_waybill_data"] = true } }, salesInvoice,
                s => string.Equals(s.TransporterId, transporterGstin, StringComparison.OrdinalIgnoreCase), "Transporter updated on the E-Way Bill.", "GST Portal did not confirm the transporter change.");
        }

        public GstResult RefreshEWayBill(string salesInvoice)
        {
            var s = GetStatus(salesInvoice);
            if (!s.HasEwb) return new GstResult { Success = false, Message = "No E-Way Bill for this invoice.", Status = s };
            return Call(EWaybillModule + "fetch_e_waybill_data", new JObject { ["doctype"] = "Sales Invoice", ["docname"] = salesInvoice, ["attach"] = false, ["force"] = true }, salesInvoice,
                x => x.HasEwb, "E-Way Bill data refreshed from the portal.", "E-Way Bill not found after refresh.");
        }

        private static JObject TransportValues(EWayBillInput t)
        {
            var v = new JObject
            {
                ["gst_transporter_id"] = t.TransporterId ?? "", ["vehicle_no"] = (t.VehicleNo ?? "").Replace(" ", "").ToUpperInvariant(), ["distance"] = t.Distance,
                ["mode_of_transport"] = t.ModeOfTransport ?? "Road", ["gst_vehicle_type"] = t.VehicleType ?? "Regular"
            };
            if (!string.IsNullOrWhiteSpace(t.LrNo)) v["lr_no"] = t.LrNo;
            if (!string.IsNullOrWhiteSpace(t.LrDate)) v["lr_date"] = t.LrDate;
            return v;
        }

        /// <summary>
        /// Calls an installed GST method, then RE-READS ERPNext to decide success. A timeout is never blindly retried: the status is re-read and,
        /// if the outcome cannot be confirmed, the result is flagged Uncertain.
        /// </summary>
        private GstResult Call(string method, JObject args, string salesInvoice, Func<GstStatus, bool> confirmed, string okMsg, string notConfirmedMsg)
        {
            string callError = null; bool unavailable = false;
            try { _erp.CallPost(method, args); }
            catch (ErpUnavailableException ex) { callError = ex.Message; unavailable = true; }
            catch (ErpApiException ex) { callError = ex.Message; }
            catch (ErpAuthException ex) { callError = "GST Portal rejected the credentials/permissions: " + ex.Message; }

            GstStatus after = null;
            try { after = GetStatus(salesInvoice, true); } catch (ErpUnavailableException ex) { if (callError == null) callError = ex.Message; unavailable = true; }
            Logger.Info("gst.call", "method", method.Substring(method.LastIndexOf('.') + 1), "invoice", salesInvoice, "error", callError ?? "-", "confirmed", after != null && confirmed(after));

            if (after != null && confirmed(after)) return new GstResult { Success = true, Message = okMsg, Status = after };
            if (unavailable) return new GstResult { Success = false, Uncertain = true, Status = after, Message = "The outcome could not be confirmed (" + callError + "). Nothing was retried. Use Refresh Status before trying again." };
            return new GstResult { Success = false, Status = after, Message = callError ?? notConfirmedMsg };
        }

        // =================================================================== detailed validation reports (read-only; never generate anything)
        public sealed class CheckItem { public string Name, Message; public bool Ok, Warning; }

        private static void Add(List<CheckItem> l, string name, bool ok, string okMsg, string failMsg, bool warnOnly = false)
        {
            l.Add(new CheckItem { Name = name, Ok = ok || warnOnly, Warning = !ok && warnOnly, Message = ok ? okMsg : failMsg });
        }

        private const string GstinRx = @"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$";

        public List<CheckItem> ValidateEInvoiceReport(string salesInvoice)
        {
            var r = new List<CheckItem>();
            string user;
            try { user = (string)_erp.Method("frappe.auth.get_logged_user")["message"]; }
            catch (Exception ex) { r.Add(new CheckItem { Name = "GST Portal connection", Ok = false, Message = ex.Message }); return r; }
            Add(r, "GST Portal connection", true, "Connected as " + user, "");

            JObject co = null, si = null;
            try { co = _erp.GetDoc("Company", _cfg.ErpCompany); } catch (Exception ex) { r.Add(new CheckItem { Name = "Active configuration / company", Ok = false, Message = ex.Message }); }
            Add(r, "Active configuration / company", co != null && string.Equals((string)co["abbr"], _cfg.ErpAbbr, StringComparison.Ordinal),
                "Configuration " + _cfg.Name + " -> company " + _cfg.ErpCompany, "Company " + _cfg.ErpCompany + " not found on the GST Portal site or its abbreviation differs.");
            try { si = _erp.GetDoc("Sales Invoice", salesInvoice); } catch (Exception ex) { r.Add(new CheckItem { Name = "GST Portal invoice mapping", Ok = false, Message = ex.Message }); return r; }
            bool mapped = si != null && string.Equals(Str(si["company"]), _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase);
            Add(r, "GST Portal invoice mapping", mapped, "Linked to " + salesInvoice + " (" + _cfg.ErpCompany + ")",
                si == null ? "Sales Invoice " + salesInvoice + " does not exist." : "Invoice belongs to company " + Str(si["company"]) + ", not the configured company.");
            if (!mapped) return r;

            int ds = si["docstatus"] == null ? 0 : (int)si["docstatus"];
            Add(r, "Invoice submission status", ds == 1, "Submitted", ds == 0 ? "Draft - submit the invoice in GST Portal first." : "Cancelled.");
            string cg = Str(si["company_gstin"]), bg = Str(si["billing_address_gstin"]), pos = Str(si["place_of_supply"]);
            Add(r, "Supplier GSTIN", !string.IsNullOrWhiteSpace(cg) && System.Text.RegularExpressions.Regex.IsMatch(cg, GstinRx), cg, "Missing or invalid on the invoice (company GSTIN).");
            if (!string.IsNullOrWhiteSpace(_cfg.SupplierGstin) && !string.IsNullOrWhiteSpace(cg))
                Add(r, "Supplier GSTIN matches configuration", string.Equals(cg, _cfg.SupplierGstin.Trim(), StringComparison.OrdinalIgnoreCase), "Same as GST Configuration",
                    "Invoice GSTIN " + cg + " differs from the configured " + _cfg.SupplierGstin + ".");
            bool overseas = pos == "96-Other Countries";
            Add(r, "Recipient GSTIN", overseas || (!string.IsNullOrWhiteSpace(bg) && System.Text.RegularExpressions.Regex.IsMatch(bg, GstinRx)),
                overseas ? "Export (no GSTIN needed)" : bg, "Missing or invalid billing GSTIN: e-Invoice is not applicable to B2C invoices.");
            Add(r, "Document number", System.Text.RegularExpressions.Regex.IsMatch(salesInvoice, @"^[A-Za-z0-9][A-Za-z0-9/-]{0,15}$"), salesInvoice,
                salesInvoice + " is not accepted by the IRP (max 16 characters: letters, digits, slash, hyphen). Change the GST Portal naming series.");
            DateTime pd; bool hasDate = DateTime.TryParse(Str(si["posting_date"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out pd);
            Add(r, "Document date", hasDate && pd.Date <= DateTime.Today, hasDate ? pd.ToString("yyyy-MM-dd") : "", "Missing or in the future.");

            var gs = _erp.GetDoc("GST Settings", "GST Settings");
            if (gs != null && hasDate && gs["e_invoice_reporting_time_limit_days"] != null && Convert.ToInt32(gs["e_invoice_reporting_time_limit_days"].ToString(), CultureInfo.InvariantCulture) > 0)
            {
                int lim = Convert.ToInt32(gs["e_invoice_reporting_time_limit_days"].ToString(), CultureInfo.InvariantCulture);
                Add(r, "Reporting time limit", DateTime.Today <= pd.AddDays(lim), "Within " + lim + " days", "Posting date is older than the e-Invoice reporting limit (" + lim + " days) in GST Settings.");
            }
            int minDigits = gs != null && gs["min_hsn_digits"] != null ? int.Parse(gs["min_hsn_digits"].ToString(), CultureInfo.InvariantCulture) : 6;
            var bad = ((si["items"] as JArray) ?? new JArray()).Where(i => Str(i["gst_hsn_code"]).Length < minDigits).Select(i => Str(i["item_code"])).Distinct().ToList();
            Add(r, "HSN/SAC", bad.Count == 0, "All items have HSN/SAC (min " + minDigits + " digits)", "Items without valid HSN/SAC: " + string.Join(", ", bad));
            decimal tax = Dec(si["total_taxes_and_charges"]);
            Add(r, "Tax details", tax > 0, "GST " + tax.ToString("N2", CultureInfo.InvariantCulture), "No tax amount on the invoice (check tax rows / zero-rated treatment).", warnOnly: true);
            Add(r, "Place of supply", !string.IsNullOrWhiteSpace(pos), pos, "Place of supply is not set on the invoice.");
            foreach (var kv in new[] { Tuple.Create("Billing address & PIN", Str(si["customer_address"])), Tuple.Create("Supplier address & PIN", Str(si["company_address"])) })
            {
                string pin = null;
                if (!string.IsNullOrEmpty(kv.Item2)) { var a = _erp.GetDoc("Address", kv.Item2); pin = a == null ? null : Str(a["pincode"]); }
                Add(r, kv.Item1, !string.IsNullOrEmpty(kv.Item2) && pin != null && System.Text.RegularExpressions.Regex.IsMatch(pin, @"^[1-9][0-9]{5}$"), kv.Item2 + " (PIN " + pin + ")",
                    string.IsNullOrEmpty(kv.Item2) ? "No address linked on the invoice." : "Address " + kv.Item2 + " has no valid 6-digit PIN code.");
            }
            var status = GetStatus(salesInvoice);
            var elig = ValidateForEInvoice(status);
            var eligErrors = elig.Errors.Where(e => !e.Contains("Draft") && !e.Contains("IRN already")).ToList();
            Add(r, "IRP eligibility / e-Invoice enabled", eligErrors.Count == 0, "Eligible", string.Join(" ", eligErrors));
            var cfg = ValidateGstConfiguration();
            var gspErrors = cfg.Errors.Where(e => e.Contains("GSP") || e.Contains("enable_api") || e.Contains("GSTIN")).ToList();
            Add(r, "GSP configuration", gspErrors.Count == 0, "GSP credentials and GSTIN configured", string.Join(" ", gspErrors));
            var ver = CheckVersions();
            Add(r, "india_compliance version", ver.Compatible, ver.Note, ver.Note);
            var envErrors = cfg.Errors.Where(e => e.Contains("PRODUCTION")).ToList();
            Add(r, "Sandbox / Production", envErrors.Count == 0, "Consistent (" + _cfg.Environment + ")", string.Join(" ", envErrors));
            Add(r, "Existing IRN", !status.HasIrn, "No IRN yet", "IRN already exists: " + status.Irn);
            return r;
        }

        public List<CheckItem> ValidateEWayBillReport(string salesInvoice, EWayBillInput input)
        {
            var r = new List<CheckItem>();
            try { _erp.Method("frappe.auth.get_logged_user"); Add(r, "GST Portal connection", true, "Connected", ""); }
            catch (Exception ex) { r.Add(new CheckItem { Name = "GST Portal connection", Ok = false, Message = ex.Message }); return r; }
            var s = GetStatus(salesInvoice);
            Add(r, "Invoice submission status", s.DocStatus == 1, "Submitted", "The Sales Invoice must be submitted first.");
            Add(r, "Existing E-Way Bill", !s.HasEwb, "None yet", "E-Way Bill " + s.EwbNo + " already exists.");
            var c = ValidateForEWayBill(s, input, true);
            foreach (var e in c.Errors.Where(x => !x.Contains("Submitted") && !x.Contains("already exists"))) r.Add(new CheckItem { Name = "Transport / eligibility", Ok = false, Message = e });
            foreach (var w in c.Warnings) r.Add(new CheckItem { Name = "Note", Ok = true, Warning = true, Message = w });
            var cfg = ValidateGstConfiguration();
            Add(r, "GST / GSP configuration", cfg.Ok, "Configured", string.Join(" ", cfg.Errors));
            return r;
        }

        // =================================================================== documents
        public byte[] DownloadInvoicePdf(string salesInvoice)
        {
            GetStatus(salesInvoice);   // company check
            return _erp.GetBytes("/api/method/frappe.utils.print_format.download_pdf",
                new Dictionary<string, string> { { "doctype", "Sales Invoice" }, { "name", salesInvoice }, { "no_letterhead", "0" } });
        }


        public byte[] DownloadEWayBillPdf(string salesInvoice)
        {
            var s = GetStatus(salesInvoice);
            if (string.IsNullOrEmpty(s.EwbNo)) throw new ConfigurationException("This invoice has no E-Way Bill.");
            return _erp.GetBytes("/api/method/frappe.utils.print_format.download_pdf",
                new Dictionary<string, string> { { "doctype", "e-Waybill Log" }, { "name", s.EwbNo }, { "no_letterhead", "1" } });
        }

        /// <summary>Output tax accounts the GST app itself uses for this company (GST Settings -> GST Accounts); used to pre-fill the tax mapping.</summary>
        public Dictionary<string, string> SuggestGstAccounts(out string cessAccount)
        {
            cessAccount = null;
            var map = new Dictionary<string, string>();
            var gs = _erp.GetDoc("GST Settings", "GST Settings");
            var rows = gs == null ? null : gs["gst_accounts"] as JArray;
            if (rows == null) return map;
            var r = rows.FirstOrDefault(x => string.Equals((string)x["company"], _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase) && string.Equals((string)x["account_type"], "Output", StringComparison.OrdinalIgnoreCase));
            if (r == null) return map;
            if (!string.IsNullOrEmpty((string)r["cgst_account"])) map["-100"] = (string)r["cgst_account"];
            if (!string.IsNullOrEmpty((string)r["sgst_account"])) map["-110"] = (string)r["sgst_account"];
            if (!string.IsNullOrEmpty((string)r["igst_account"])) map["-120"] = (string)r["igst_account"];
            cessAccount = (string)r["cess_account"];
            return map;
        }
        // =================================================================== SAP-side record
        public GstRecord ToRecord(int docEntry, int docNum, GstStatus s)
        {
            return new GstRecord
            {
                DocEntry = docEntry, DocNum = docNum, ErpInvoice = s.SalesInvoice, Customer = s.CustomerName, Gstin = s.BillingGstin, InvoiceDate = s.PostingDate,
                Taxable = s.NetTotal, Gst = s.TaxTotal,
                EInvStatus = s.HasIrn ? "Generated" : (s.IrnCancelled ? "Cancelled" : (string.IsNullOrEmpty(s.EInvoiceStatus) ? "Not generated" : s.EInvoiceStatus)),
                Irn = s.Irn, AckNo = s.AckNo, AckDate = s.AckDate.HasValue ? s.AckDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                SignedQr = s.SignedQr, SignedInvoice = s.SignedInvoice,
                CancelStatus = s.IrnCancelled || s.EwbCancelled ? ((s.IrnCancelled ? "IRN cancelled " : "") + (s.EwbCancelled ? "EWB cancelled" : "")).Trim() : "",
                EwbStatus = s.EwbStatus, EwbNo = s.EwbNo, EwbDate = s.EwbDate.HasValue ? s.EwbDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                EwbValid = s.EwbValidUpto.HasValue ? s.EwbValidUpto.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "",
                Transporter = string.IsNullOrEmpty(s.TransporterName) ? s.TransporterId : s.TransporterName, Vehicle = s.VehicleNo, Mode = s.ModeOfTransport, Distance = s.Distance,
                LastSync = DateTime.Now
            };
        }
    }
}
