using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EInvGstAddon.Common;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.Core
{
    public sealed class BuildResult
    {
        public JObject Json;
        public List<string> Errors = new List<string>();        // problems that make a correct contract impossible (never silently ignored)
        public List<string> Notes = new List<string>();
        public bool Ok { get { return Errors.Count == 0; } }
    }

    /// <summary>
    /// SAP invoice (read through the DI API) -> generic GST invoice contract of the ERPNext GST API.
    /// Original SAP values are carried over exactly; nothing is invented. Whatever cannot be mapped is reported in Errors instead of being guessed.
    /// </summary>
    public static class InvoiceJsonBuilder
    {
        private static readonly Regex Gstin = new Regex(@"^[0-9]{2}[A-Z0-9]{10}[0-9A-Z]{3}$");

        public static string DocumentNumber(GstSource s, AppConfig cfg)
        {
            string p = string.IsNullOrWhiteSpace(cfg.DocNumberPattern) ? "{DocNum}" : cfg.DocNumberPattern;
            return p.Replace("{SeriesName}", s.SeriesName ?? "").Replace("{DocNum}", s.DocNum.ToString(CultureInfo.InvariantCulture)).Replace("{DocEntry}", s.DocEntry.ToString(CultureInfo.InvariantCulture));
        }

        private static string StateCode(GstParty p, AppConfig cfg)
        {
            if (!string.IsNullOrEmpty(p.Gstin) && Gstin.IsMatch(p.Gstin)) return p.Gstin.Substring(0, 2);          // the GSTIN is authoritative
            string code;
            if (cfg.StateCodeMap.TryGetValue(p.StateCode ?? "", out code)) return code;
            return IndiaStates.ByAbbreviation.TryGetValue(p.StateCode ?? "", out code) ? code : "";
        }

        /// <summary>
        /// E-Way Bill only: the consignee (Ship-To) state must come from the Ship-To address SAVED ON THE INVOICE (INV12), falling back to the customer master only when the invoice has none.
        /// A foreign consignee is not forced into an Indian state (the portal receives its "other country" code); an Indian one must resolve to a real GST state code. Nothing is defaulted.
        /// </summary>
        private static void CheckConsignee(GstSource s, AppConfig cfg, BuildResult r, string pos)
        {
            var party = s.ShipTo ?? s.Buyer;
            string country = !string.IsNullOrEmpty(s.ShipSnapCountry) ? s.ShipSnapCountry : (party.CountryIso2 ?? "").ToUpperInvariant();
            bool snapState = !string.IsNullOrEmpty(s.ShipSnapState);
            string state = snapState ? s.ShipSnapState : (party.StateCode ?? "").ToUpperInvariant();
            string source = snapState ? "INV12.StateS" : "CRD1.State (invoice has no saved Ship-To state)";
            bool foreign = !string.IsNullOrEmpty(country) && country != "IN";
            string resolved = foreign ? "(foreign)" : StateCode(new GstParty { Gstin = party.Gstin, StateCode = state }, cfg);
            Logger.Info("ewb.consignee", "docEntry", s.DocEntry, "docNum", s.DocNum, "country", country, "state", state, "resolved", resolved, "source", source, "placeOfSupply", pos, "finalPayloadState", foreign ? "other-country" : resolved);
            if (foreign) return;
            if (string.IsNullOrEmpty(state) && string.IsNullOrEmpty(resolved))
                r.Errors.Add("Consignee Ship-To State Code is missing or invalid. Please check the Ship-To address in the SAP Business One invoice.");
            else if (string.IsNullOrEmpty(resolved))
                r.Errors.Add("The Ship-To state '" + state + "' of the invoice has no GST state code mapping. Add it to the state mapping in GST Configuration, or correct the Ship-To address in the SAP Business One invoice.");
        }

        private static JObject Party(GstParty p, AppConfig cfg, bool includeGstin = true)
        {
            string a1 = string.Join(", ", new[] { p.Building, p.StreetNo, p.Street, p.Block }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var o = new JObject { ["legal_name"] = p.Name ?? "", ["address"] = a1, ["address2"] = p.Address2 ?? "", ["city"] = p.City ?? "", ["state_code"] = StateCode(p, cfg), ["pincode"] = (p.ZipCode ?? "").Trim() };
            if (includeGstin) o["gstin"] = p.Gstin ?? "";
            return o;
        }

        private static string Unit(string uom, AppConfig cfg)
        {
            string u = (uom ?? "").Trim();
            if (u.Length == 0) return "OTH";
            string m;
            return cfg.UqcMap.TryGetValue(u, out m) ? m.ToUpperInvariant() : u.ToUpperInvariant();                 // never replaced silently: the server rejects unknown units
        }

        public static BuildResult Build(GstSource s, AppConfig cfg, TransportInput transport = null)
        {
            var r = new BuildResult();
            if (s.Cancelled) r.Errors.Add("The SAP invoice is cancelled.");
            if (!string.Equals(s.DocType, "I", StringComparison.OrdinalIgnoreCase)) r.Notes.Add("Service invoice (no item lines): service lines are reported with their SAC code.");
            string sellerGstin = (cfg.SupplierGstin ?? "").Trim().ToUpperInvariant();
            if (sellerGstin.Length == 0) r.Errors.Add("The seller GSTIN is not configured (GST Configuration).");

            bool export = !string.Equals(s.Buyer.CountryIso2, "IN", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(s.Buyer.CountryIso2);
            decimal hdrDisc = s.HeaderDiscountPercent;

            var items = new JArray();
            decimal sumTaxable = 0, sumCgst = 0, sumSgst = 0, sumIgst = 0, sumCess = 0;
            var unmapped = new List<string>();
            int line = 0;
            foreach (var l in s.Lines)
            {
                line++;
                var rows = s.Taxes.Where(t => t.RelateType == 1 && t.LineNum == l.LineNum).ToList();
                decimal cgst = 0, sgst = 0, igst = 0, cess = 0, cgstR = 0, sgstR = 0, igstR = 0, cessR = 0, baseGst = 0;
                foreach (var t in rows)
                {
                    string kind;
                    if (t.AmountLc == 0 && t.Rate == 0 && !cfg.GstTaxTypeMap.ContainsKey(t.TaxType)) continue;
                    if (!cfg.GstTaxTypeMap.TryGetValue(t.TaxType, out kind))
                    {
                        if (t.AmountLc != 0 && !cfg.OtherChargeTaxTypes.Contains(t.TaxType)) unmapped.Add(t.TaxType + " (" + t.TaxCode + ")");
                        continue;
                    }
                    switch (kind)
                    {
                        case "cgst": cgst += t.AmountLc; cgstR = t.Rate; break;
                        case "sgst": sgst += t.AmountLc; sgstR = t.Rate; break;
                        case "igst": igst += t.AmountLc; igstR = t.Rate; break;
                        case "cess": cess += t.AmountLc; cessR = t.Rate; break;
                    }
                    if (kind != "cess" && t.BaseLc != 0) baseGst = t.BaseLc;
                }
                decimal taxable = baseGst != 0 ? baseGst : Math.Round(l.LineTotalLc * (1 - hdrDisc / 100m), 2, MidpointRounding.AwayFromZero);
                string hsn = !string.IsNullOrEmpty(l.Hsn) ? l.Hsn : l.Sac;
                items.Add(new JObject
                {
                    ["line"] = line, ["item_code"] = l.ItemCode ?? "", ["description"] = l.Description ?? "", ["hsn_code"] = hsn ?? "", ["quantity"] = l.Quantity, ["unit"] = Unit(l.Uom, cfg),
                    ["unit_price"] = l.UnitPrice, ["discount"] = 0, ["taxable_value"] = taxable,
                    ["cgst_rate"] = cgstR, ["sgst_rate"] = sgstR, ["igst_rate"] = igstR, ["cess_rate"] = cessR,
                    ["cgst_amount"] = cgst, ["sgst_amount"] = sgst, ["igst_amount"] = igst, ["cess_amount"] = cess
                });
                sumTaxable += taxable; sumCgst += cgst; sumSgst += sgst; sumIgst += igst; sumCess += cess;
            }
            if (unmapped.Count > 0)
                r.Errors.Add("SAP tax type(s) without a GST mapping: " + string.Join(", ", unmapped.Distinct()) + ". Map them in 'GST Tax Mapping' (cgst/sgst/igst/cess) or list them as other charges (e.g. TCS). Nothing was dropped silently.");

            // freight and other charges
            decimal other = 0;
            foreach (var e in s.Expenses) other += e.AmountLc;
            foreach (var t in s.Taxes.Where(t => cfg.OtherChargeTaxTypes.Contains(t.TaxType))) other += t.AmountLc;           // e.g. TCS collected at source
            var taxedFreight = s.Taxes.Where(t => t.RelateType == 3 && cfg.GstTaxTypeMap.ContainsKey(t.TaxType) && t.AmountLc != 0).ToList();
            if (taxedFreight.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(cfg.FreightSac)) r.Errors.Add("Freight carries GST in SAP. Configure the freight SAC (e.g. a 99xxxx service code) in 'GST Tax Mapping' so it can be reported as its own line.");
                else
                {
                    decimal fcg = 0, fsg = 0, fig = 0, fcr = 0, fsr = 0, fir = 0, fbase = s.Expenses.Sum(e => e.AmountLc);
                    foreach (var t in taxedFreight)
                    {
                        string kind = cfg.GstTaxTypeMap[t.TaxType];
                        if (kind == "cgst") { fcg += t.AmountLc; fcr = t.Rate; } else if (kind == "sgst") { fsg += t.AmountLc; fsr = t.Rate; } else if (kind == "igst") { fig += t.AmountLc; fir = t.Rate; }
                    }
                    items.Add(new JObject { ["line"] = ++line, ["item_code"] = "FREIGHT", ["description"] = "Freight", ["hsn_code"] = cfg.FreightSac, ["quantity"] = 1, ["unit"] = "OTH", ["unit_price"] = fbase, ["discount"] = 0, ["taxable_value"] = fbase,
                        ["cgst_rate"] = fcr, ["sgst_rate"] = fsr, ["igst_rate"] = fir, ["cess_rate"] = 0, ["cgst_amount"] = fcg, ["sgst_amount"] = fsg, ["igst_amount"] = fig, ["cess_amount"] = 0 });
                    sumTaxable += fbase; sumCgst += fcg; sumSgst += fsg; sumIgst += fig; other -= fbase;
                }
            }

            string sellerState = !string.IsNullOrEmpty(cfg.StateCode) ? cfg.StateCode : (sellerGstin.Length >= 2 ? sellerGstin.Substring(0, 2) : "");
            var seller = new JObject { ["gstin"] = sellerGstin, ["legal_name"] = cfg.LegalName ?? "", ["trade_name"] = cfg.TradeName ?? "", ["address"] = cfg.GstAddress ?? "", ["city"] = cfg.GstCity ?? "", ["state_code"] = sellerState, ["pincode"] = cfg.GstPin ?? "" };
            var buyer = Party(s.Buyer, cfg);
            if (export) { buyer["gstin"] = "URP"; buyer["state_code"] = "96"; buyer["pincode"] = ""; }
            string pos = export ? "96" : (!string.IsNullOrEmpty(s.ShipStateCode) ? StateCode(new GstParty { StateCode = s.ShipStateCode }, cfg) : "");
            if (transport != null) CheckConsignee(s, cfg, r, pos);
            if (string.IsNullOrEmpty(pos)) pos = (string)buyer["state_code"];

            string currency = cfg.MapCurrency(s.DocCurrency);
            var json = new JObject
            {
                ["integration_id"] = cfg.IntegrationId, ["sap_database"] = s.Database, ["object_type"] = s.ObjType, ["doc_entry"] = s.DocEntry, ["doc_num"] = s.DocNum, ["document_type"] = "INV",
                ["document_number"] = DocumentNumber(s, cfg), ["document_date"] = s.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["document_due_date"] = s.DueDate == DateTime.MinValue ? null : s.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["currency"] = string.IsNullOrEmpty(currency) ? "INR" : currency, ["exchange_rate"] = s.ExchangeRate, ["environment"] = cfg.Environment,
                ["supply_type"] = export ? (cfg.ExportWithPayment ? "EXPWP" : "EXPWOP") : "B2B", ["place_of_supply"] = pos,
                ["seller"] = seller, ["buyer"] = buyer, ["items"] = items,
                ["tax_totals"] = new JObject
                {
                    ["taxable_value"] = sumTaxable, ["cgst"] = sumCgst, ["sgst"] = sumSgst, ["igst"] = sumIgst, ["cess"] = sumCess, ["discount"] = 0,
                    ["other_charges"] = other, ["round_off"] = s.RoundOffLc, ["invoice_total"] = s.DocTotalLc, ["invoice_total_fc"] = s.ForeignCurrency ? s.DocTotalFc : 0
                }
            };
            if (s.ShipTo != null) json["ship_to"] = Party(s.ShipTo, cfg);
            if (export && transport != null)
            {
                // The e-Way Bill of an export covers the movement inside India: the actual Ship To must be an Indian delivery point taken from SAP's own E-Way Bill Ship To details.
                var e = s.EwbShipTo;
                string ec = e == null ? "" : (Regex.IsMatch(e.StateCode ?? "", @"^[0-9]{2}$") ? e.StateCode : StateCode(new GstParty { StateCode = e.StateCode }, cfg));
                bool okTo = e != null && Regex.IsMatch((e.ZipCode ?? "").Trim(), @"^[0-9]{6}$") && e.ZipCode.Trim() != "999999" && ec != "" && ec != "96" && ec != "97" && ec != "99" && !string.IsNullOrWhiteSpace(e.City);
                Logger.Info("ewb.exportshipto", "docEntry", s.DocEntry, "docNum", s.DocNum, "place", e == null ? "" : e.City, "pin", e == null ? "" : e.ZipCode, "state", ec, "source", "INV26.ToPlace/ToZipCode/ActToState", "ok", okTo);
                if (!okTo) r.Errors.Add("For an export invoice the E-Way Bill covers the movement inside India. Please enter the Ship To (delivery place, 6-digit PIN code and state of the port, airport or delivery point in India) in the E-Way Bill Details of the SAP Business One invoice (Electronic Documents tab > E-Way Bill Details > Ship To).");
                else json["ship_to"] = Party(new GstParty { Name = s.Buyer.Name, Street = e.Street, Address2 = e.Address2, City = e.City, ZipCode = e.ZipCode.Trim(), StateCode = ec, Gstin = "URP" }, cfg);
                if (okTo) { var st = (JObject)json["ship_to"]; st["state_code"] = ec; if (string.IsNullOrEmpty((string)st["address"])) st["address"] = e.City; }
            }
            if (export) json["export"] = new JObject { ["country_code"] = s.Buyer.CountryIso2 ?? "", ["foreign_currency"] = currency, ["port_code"] = cfg.ExportPortCode ?? "" };
            if (transport != null)
                json["transport"] = new JObject
                {
                    ["transporter_id"] = transport.TransporterId ?? "", ["transporter_name"] = transport.TransporterName ?? "", ["vehicle_number"] = transport.VehicleNo ?? "", ["vehicle_type"] = string.IsNullOrEmpty(transport.VehicleType) ? "Regular" : transport.VehicleType,
                    ["transport_mode"] = transport.Mode ?? "", ["distance"] = transport.Distance, ["doc_number"] = transport.DocNo ?? "", ["doc_date"] = string.IsNullOrEmpty(transport.DocDate) ? null : transport.DocDate
                };
            r.Json = json;
            return r;
        }
    }
}
