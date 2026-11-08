using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    /// <summary>Builds and creates the Draft ERPNext Sales Invoice. Never submits and never calls any e-Invoice / e-Way Bill method.</summary>
    public sealed class InvoiceSync
    {
        private readonly ErpClient _erp; private readonly ISapSource _sap; private readonly ErpLookups _lk;
        private readonly CustomerSync _cust; private readonly ItemSync _items; private readonly WarehouseSync _whs; private readonly IStateStore _store; private readonly AppConfig _cfg;

        public InvoiceSync(ErpClient erp, ISapSource sap, IStateStore store, ErpLookups lk, CustomerSync cust, ItemSync items, WarehouseSync whs)
        { _erp = erp; _sap = sap; _store = store; _lk = lk; _cust = cust; _items = items; _whs = whs; _cfg = erp.Config; }

        /// <summary>Looks for an existing Sales Invoice of the configured company with this SAP reference.</summary>
        public JObject FindExisting(string syncReference)
        {
            var l = _erp.GetList("Sales Invoice", new[] { "name", "docstatus", "company", "customer", "posting_date", "grand_total", "rounded_total", "outstanding_amount", "creation" },
                new object[] { new object[] { "custom_sap_sync_reference", "=", syncReference }, new object[] { "company", "=", _cfg.ErpCompany } }, 5);
            return l.Count == 0 ? null : (JObject)l[0];
        }

        public JObject CreateDraft(SapInvoice inv, SyncOutcome outcome)
        {
            if (inv.Cancelled) throw new ConfigurationException("SAP invoice " + inv.DocNum + " is cancelled; it is not synchronised.");
            if (!string.Equals(inv.DocType, "I", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("SAP invoice " + inv.DocNum + " is a service invoice (no item lines); service invoices are not supported in Phase 1.");
            if (inv.Lines.Count == 0) throw new ConfigurationException("SAP invoice " + inv.DocNum + " has no item lines.");

            var bp = _sap.GetPartner(inv.CardCode);
            string customer = _cust.Ensure(bp, outcome);

            // addresses used on this invoice
            string billAddr = null, shipAddr = null;
            try
            {
                billAddr = _cust.EnsureAddress(customer, bp, _sap.GetAddress(bp.CardCode, inv.PayToCode, "B"), "Billing", outcome);
                if (!string.IsNullOrEmpty(inv.ShipToCode)) shipAddr = _cust.EnsureAddress(customer, bp, _sap.GetAddress(bp.CardCode, inv.ShipToCode, "S"), "Shipping", outcome);
            }
            catch (ErpUnavailableException) { throw; }
            catch (ErpAuthException) { throw; }
            catch (Exception ex) { outcome.Warnings.Add("Invoice address not linked: " + ex.Message); }

            string currency = _cfg.MapCurrency(inv.Currency);
            if (string.IsNullOrEmpty(currency) || currency.Length != 3) throw new ConfigurationException("SAP currency '" + inv.Currency + "' cannot be mapped to an ISO code. Add it to CurrencyMap in the configuration.");
            _lk.RequireEnabledCurrency(currency);

            // price list currency: the price list's own currency, converted at 1 when it is the company currency or the invoice currency
            string plCurrency = _lk.PriceListCurrency(_cfg.SellingPriceList);
            decimal plRate;
            if (string.Equals(plCurrency, currency, StringComparison.OrdinalIgnoreCase)) plRate = string.Equals(currency, _lk.CompanyCurrency, StringComparison.OrdinalIgnoreCase) ? 1m : inv.ExchangeRate;
            else if (string.Equals(plCurrency, _lk.CompanyCurrency, StringComparison.OrdinalIgnoreCase)) plRate = 1m;
            else throw new ConfigurationException("Selling price list '" + _cfg.SellingPriceList + "' is in " + plCurrency + ", which is neither the company currency (" + _lk.CompanyCurrency + ") nor the invoice currency (" + currency + "). Choose another price list in the settings.");

            var rows = new JArray();
            foreach (var l in inv.Lines)
            {
                if (string.IsNullOrEmpty(l.ItemCode)) throw new ConfigurationException("Line " + (l.LineNum + 1) + " of SAP invoice " + inv.DocNum + " has no item code.");
                string itemCode = _items.Ensure(l.ItemCode, outcome);
                string wh = string.IsNullOrEmpty(l.WarehouseCode) ? null : _whs.Ensure(l.WarehouseCode, outcome);
                string uom = _lk.EnsureUom(l.Uom);
                var row = new JObject
                {
                    ["item_code"] = itemCode,
                    ["item_name"] = Util.Trunc(string.IsNullOrWhiteSpace(l.ItemName) ? l.ItemCode : l.ItemName, 140),
                    ["description"] = (string.IsNullOrWhiteSpace(l.Description) ? l.ItemName : l.Description) + (R(l.UnitPrice, _cfg.RatePrecision) != l.UnitPrice ? " [SAP unit price " + l.UnitPrice.ToString("0.######", CultureInfo.InvariantCulture) + "]" : ""),
                    ["qty"] = l.Quantity,
                    ["uom"] = uom,
                    ["conversion_factor"] = 1,
                    ["price_list_rate"] = l.UnitPrice,
                    ["discount_percentage"] = l.DiscountPercent,
                    ["custom_sap_line_num"] = (l.LineNum + 1).ToString(CultureInfo.InvariantCulture),
                    ["custom_sap_tax_code"] = l.TaxCode
                };
                if (wh != null) row["warehouse"] = wh;
                rows.Add(row);
            }

            var taxes = BuildTaxes(inv);

            // ERPNext holds rates at a fixed precision (e.g. 2 decimals) while SAP allows 0.158. Predict the loss and carry the
            // exact difference on the round-off account so the ERPNext totals equal the SAP totals; always reported as a warning.
            decimal precisionDiff = PrecisionDifference(inv, _cfg.RatePrecision);
            if (precisionDiff != 0)
            {
                RequireAccount(_cfg.RoundOffAccount, "unit price precision difference");
                taxes.Add(Row(_cfg.RoundOffAccount, "SAP unit price precision difference (ERPNext rate precision " + _cfg.RatePrecision + ")", precisionDiff));
                outcome.Warnings.Add("ERPNext rate precision (" + _cfg.RatePrecision + " decimals) cannot hold some SAP unit prices; difference of " + precisionDiff.ToString("0.00", CultureInfo.InvariantCulture) + " carried on " + _cfg.RoundOffAccount + " so totals equal SAP.");
            }

            string remarks = "SAP " + inv.Database + " A/R Invoice " + inv.DocNum + " (DocEntry " + inv.DocEntry + ")";
            if (!string.IsNullOrEmpty(inv.PaymentTermsName)) remarks += " | Payment terms: " + inv.PaymentTermsName;
            if (!string.IsNullOrEmpty(inv.Salesperson)) remarks += " | Salesperson: " + inv.Salesperson;
            if (!string.IsNullOrEmpty(inv.Remarks)) remarks += " | " + inv.Remarks;

            var doc = new JObject
            {
                ["doctype"] = "Sales Invoice",
                ["company"] = _cfg.ErpCompany,
                ["customer"] = customer,
                ["set_posting_time"] = 1,
                ["posting_date"] = inv.PostingDate.ToString("yyyy-MM-dd"),
                ["due_date"] = (inv.DueDate < inv.PostingDate ? inv.PostingDate : inv.DueDate).ToString("yyyy-MM-dd"),
                ["currency"] = currency,
                ["conversion_rate"] = string.Equals(currency, _lk.CompanyCurrency, StringComparison.OrdinalIgnoreCase) ? 1m : inv.ExchangeRate,
                ["selling_price_list"] = _cfg.SellingPriceList,
                ["price_list_currency"] = plCurrency,
                ["plc_conversion_rate"] = plRate,
                ["update_stock"] = 0,
                ["remarks"] = Util.Trunc(remarks, 1000),
                ["items"] = rows,
                ["taxes"] = taxes,
                ["custom_sap_database"] = inv.Database,
                ["custom_sap_docentry"] = inv.DocEntry.ToString(CultureInfo.InvariantCulture),
                ["custom_sap_docnum"] = inv.DocNum.ToString(CultureInfo.InvariantCulture),
                ["custom_sap_object_type"] = inv.ObjType,
                ["custom_sap_sync_reference"] = inv.SyncReference,
                ["docstatus"] = 0
            };
            if (!string.IsNullOrEmpty(inv.CustomerRef)) doc["po_no"] = Util.Trunc(inv.CustomerRef, 140);
            if (!string.IsNullOrEmpty(billAddr)) doc["customer_address"] = billAddr;
            if (!string.IsNullOrEmpty(shipAddr)) doc["shipping_address_name"] = shipAddr;
            if (inv.HeaderDiscountPercent > 0) { doc["apply_discount_on"] = "Net Total"; doc["additional_discount_percentage"] = inv.HeaderDiscountPercent; }
            else if (inv.HeaderDiscount > 0) { doc["apply_discount_on"] = "Net Total"; doc["discount_amount"] = inv.HeaderDiscount; }

            if (!string.IsNullOrEmpty(inv.Salesperson) && _erp.Exists("Sales Person", inv.Salesperson))
                doc["sales_team"] = Util.Arr(new JObject { ["sales_person"] = inv.Salesperson, ["allocated_percentage"] = 100 });

            if (!string.IsNullOrWhiteSpace(_cfg.SalesInvoiceSeries)) doc["naming_series"] = _cfg.SalesInvoiceSeries;
            if (_cfg.ReferenceIncludesConfigId) doc["custom_sap_config_id"] = _cfg.Id;
            if (string.IsNullOrWhiteSpace(_lk.CompanyGstin))
                outcome.Warnings.Add("Company " + _cfg.ErpCompany + " has no GSTIN configured: GST place of supply / GST fields were not set; SAP tax amounts are preserved as tax rows for review.");
            var created = _erp.Insert("Sales Invoice", doc);
            return created;
        }

        /// <summary>Freight and tax rows with the exact SAP amounts (charge type Actual) on accounts of the configured company.</summary>
        private JArray BuildTaxes(SapInvoice inv)
        {
            var taxes = new JArray();
            if (inv.Freight != 0)
            {
                string acct = _cfg.FreightAccount;
                RequireAccount(acct, "freight");
                taxes.Add(Row(acct, "Freight (SAP: " + string.Join(", ", inv.Expenses.Select(e => e.Name)) + ")", inv.Freight));
            }

            var byType = inv.Taxes.Where(t => t.Amount != 0).GroupBy(t => t.TaxType).ToList();
            var unmapped = byType.Where(g => !_cfg.TaxAccountMap.ContainsKey(g.Key)).ToList();
            if (unmapped.Count > 0)
                throw new ConfigurationException("Tax configuration missing: SAP tax type(s) " + string.Join(", ", unmapped.Select(g => g.Key + " (" + g.First().TaxCode + ", " + g.Sum(x => x.Amount).ToString("0.00", CultureInfo.InvariantCulture) + ")")) +
                    " on invoice " + inv.DocNum + " have no ERPNext account in TaxAccountMap. Nothing was created.");
            foreach (var g in byType)
            {
                string acct = _cfg.TaxAccountMap[g.Key];
                RequireAccount(acct, "tax type " + g.Key);
                taxes.Add(Row(acct, TaxName(g.Key) + " (SAP " + g.First().TaxCode + ", " + g.First().Rate.ToString("0.##", CultureInfo.InvariantCulture) + "%)", g.Sum(x => x.Amount)));
            }
            return taxes;
        }

        private static decimal R(decimal v, int p) { return Math.Round(v, p, MidpointRounding.AwayFromZero); }

        /// <summary>SAP net (sum of SAP line totals) minus the net ERPNext will compute at its rate precision, after the header discount.</summary>
        public static decimal PrecisionDifference(SapInvoice inv, int precision)
        {
            decimal sap = 0, erp = 0;
            foreach (var l in inv.Lines)
            {
                decimal listRate = R(l.UnitPrice, precision);
                decimal rate = R(listRate * (1 - l.DiscountPercent / 100m), precision);
                erp += R(l.Quantity * rate, 2);
                sap += l.LineTotal;
            }
            decimal diff = sap - erp;
            if (inv.HeaderDiscountPercent > 0) diff = diff * (1 - inv.HeaderDiscountPercent / 100m);
            diff = R(diff, 2);
            return Math.Abs(diff) < 0.005m ? 0m : diff;
        }

        private static string TaxName(string t) { return t == "-100" ? "CGST" : t == "-110" ? "SGST" : t == "-120" ? "IGST" : "SAP tax " + t; }

        private JObject Row(string account, string desc, decimal amount)
        {
            return new JObject { ["charge_type"] = "Actual", ["account_head"] = account, ["description"] = desc, ["tax_amount"] = amount, ["add_deduct_tax"] = "Add" };
        }

        private void RequireAccount(string account, string what)
        {
            if (string.IsNullOrWhiteSpace(account)) throw new ConfigurationException("No ERPNext account is configured for " + what + ". Set it in the add-on settings (Tax && Account mapping); it is never guessed.");
            string owner = _lk.AccountCompany(account);
            if (owner == null) throw new ConfigurationException("ERPNext account '" + account + "' (" + what + ") does not exist. Create it in ERPNext or change the add-on configuration; it is never invented.");
            if (!string.Equals(owner, _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Account '" + account + "' belongs to '" + owner + "', not '" + _cfg.ErpCompany + "'. Refusing to use it.");
        }

        /// <summary>Compares ERPNext totals with SAP and returns human-readable differences.</summary>
        public static List<string> Compare(SapInvoice inv, JObject erp)
        {
            var diffs = new List<string>();
            decimal erpTotal = erp["rounded_total"] != null && (decimal)erp["rounded_total"] != 0 ? (decimal)erp["rounded_total"] : (decimal)erp["grand_total"];
            if (Math.Abs(erpTotal - inv.DocTotal) > 0.02m) diffs.Add("Total differs: SAP " + inv.DocTotal.ToString("0.00", CultureInfo.InvariantCulture) + " vs ERPNext " + erpTotal.ToString("0.00", CultureInfo.InvariantCulture));
            return diffs;
        }
    }
}
