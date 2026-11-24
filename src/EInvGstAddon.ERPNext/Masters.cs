using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    internal static class Util
    {
        public static string Trunc(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n); }
        public static string Safe(string s) { return Regex.Replace(s ?? "", @"[<>\\/%""\[\]{}`]", " ").Replace("  ", " ").Trim(); }
        public static JArray Arr(params JObject[] items) { return new JArray(items); }
    }

    /// <summary>Shared per-process caches for ERPNext lookup data (single worker thread).</summary>
    public sealed class ErpLookups
    {
        private readonly ErpClient _erp;
        private readonly HashSet<string> _groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _itemGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _uoms;
        private readonly Dictionary<string, bool> _hsn = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> _acctCompany = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _currency = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public ErpLookups(ErpClient erp) { _erp = erp; }

        // ---------- discovery (nothing site-specific is assumed) ----------
        private JObject _company;
        public JObject CompanyDoc()
        {
            if (_company == null)
            {
                _company = _erp.GetDoc("Company", _erp.Config.ErpCompany);
                if (_company == null) throw new ConfigurationException("GST Portal company '" + _erp.Config.ErpCompany + "' does not exist on " + _erp.Config.ErpUrl + ".");
            }
            return _company;
        }
        public string CompanyCurrency { get { return (string)CompanyDoc()["default_currency"]; } }
        public string CompanyGstin { get { return (string)CompanyDoc()["gstin"] ?? ""; } }

        private string FirstLeaf(string doctype, string nameField)
        {
            var l = _erp.GetList(doctype, new[] { "name" }, new object[] { new object[] { "is_group", "=", 0 } }, 1);
            if (l.Count == 0) throw new ConfigurationException("GST Portal has no " + doctype + " to use; configure a default in the add-on settings.");
            return (string)l[0]["name"];
        }
        public string LeafGroup(string doctype) { return FirstLeaf(doctype, "name"); }

        public string Territory(bool overseas)
        {
            string want = overseas ? "Rest Of The World" : "India";
            return _erp.Exists("Territory", want) ? want : FirstLeaf("Territory", "name");
        }

        public string RootWarehouse()
        {
            if (!string.IsNullOrWhiteSpace(_erp.Config.RootWarehouse)) return _erp.Config.RootWarehouse;
            var l = _erp.GetList("Warehouse", new[] { "name", "parent_warehouse" },
                new object[] { new object[] { "company", "=", _erp.Config.ErpCompany }, new object[] { "is_group", "=", 1 } }, 50);
            foreach (var w in l) if (string.IsNullOrEmpty((string)w["parent_warehouse"])) return (string)w["name"];
            throw new ConfigurationException("No root (group) warehouse found for company '" + _erp.Config.ErpCompany + "'. Set it in the add-on settings.");
        }

        private readonly Dictionary<string, string> _plCurrency = new Dictionary<string, string>();
        public string PriceListCurrency(string priceList)
        {
            string c;
            if (_plCurrency.TryGetValue(priceList, out c)) return c;
            var d = _erp.GetDoc("Price List", priceList);
            if (d == null) throw new ConfigurationException("Selling price list '" + priceList + "' does not exist in GST Portal. Choose an existing one in the add-on settings.");
            c = (string)d["currency"];
            _plCurrency[priceList] = c;
            return c;
        }

        public string EnsureUom(string sapUom, string fallback = "Nos")
        {
            string name = string.IsNullOrWhiteSpace(sapUom) ? (string.IsNullOrWhiteSpace(_erp.Config.DefaultUom) ? fallback : _erp.Config.DefaultUom) : sapUom.Trim();
            if (_uoms == null)
            {
                _uoms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in _erp.GetList("UOM", new[] { "name" }, null, 2000)) _uoms[(string)u["name"]] = (string)u["name"];
            }
            string existing;
            if (_uoms.TryGetValue(name, out existing)) return existing;
            _erp.Insert("UOM", new JObject { ["uom_name"] = name, ["enabled"] = 1 });
            _uoms[name] = name;
            Logger.Info("erp.uom.created", "uom", name);
            return name;
        }

        public string EnsureCustomerGroup(string sapGroup, string fallback)
        {
            string g = !string.IsNullOrWhiteSpace(sapGroup) ? Util.Safe(sapGroup) : (!string.IsNullOrWhiteSpace(fallback) ? fallback : LeafGroup("Customer Group"));
            if (_groups.Contains(g)) return g;
            var existing = _erp.GetDoc("Customer Group", g);
            if (existing == null)
            {
                _erp.Insert("Customer Group", new JObject { ["customer_group_name"] = g, ["parent_customer_group"] = "All Customer Groups", ["is_group"] = 0 });
                Logger.Info("erp.customergroup.created", "group", g);
            }
            _groups.Add(g);
            return g;
        }

        public string EnsureItemGroup(string sapGroup)
        {
            string g = !string.IsNullOrWhiteSpace(sapGroup) ? Util.Safe(sapGroup) : (!string.IsNullOrWhiteSpace(_erp.Config.DefaultItemGroup) ? _erp.Config.DefaultItemGroup : LeafGroup("Item Group"));
            if (_itemGroups.Contains(g)) return g;
            if (_erp.GetDoc("Item Group", g) == null)
            {
                _erp.Insert("Item Group", new JObject { ["item_group_name"] = g, ["parent_item_group"] = "All Item Groups", ["is_group"] = 0 });
                Logger.Info("erp.itemgroup.created", "group", g);
            }
            _itemGroups.Add(g);
            return g;
        }

        /// <summary>True if the HSN/SAC code exists in the India Compliance "GST HSN Code" master. Never creates one.</summary>
        public bool HsnExists(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            bool ok;
            if (_hsn.TryGetValue(code, out ok)) return ok;
            ok = _erp.GetDoc("GST HSN Code", code) != null;
            _hsn[code] = ok;
            return ok;
        }

        /// <summary>Returns the account's company, or null if the account does not exist.</summary>
        public string AccountCompany(string account)
        {
            string c;
            if (_acctCompany.TryGetValue(account, out c)) return c;
            var d = _erp.GetDoc("Account", account);
            c = d == null ? null : (string)d["company"];
            _acctCompany[account] = c;
            return c;
        }

        public void RequireEnabledCurrency(string iso)
        {
            bool ok;
            if (!_currency.TryGetValue(iso, out ok))
            {
                var d = _erp.GetDoc("Currency", iso);
                ok = d != null && (int)d["enabled"] == 1;
                _currency[iso] = ok;
            }
            if (!ok) throw new ConfigurationException("Currency '" + iso + "' does not exist or is disabled in GST Portal. Enable it in GST Portal (Currency list) before synchronising this invoice.");
        }
    }

    public sealed class CustomerSync
    {
        private readonly ErpClient _erp; private readonly IStateStore _store; private readonly ISapSource _sap; private readonly ErpLookups _lk; private readonly AppConfig _cfg;
        public CustomerSync(ErpClient erp, IStateStore store, ISapSource sap, ErpLookups lk) { _erp = erp; _store = store; _sap = sap; _lk = lk; _cfg = erp.Config; }

        /// <summary>
        /// Resolves the ERPNext customer for a SAP business partner: stored mapping -> ERPNext custom-field link -> create.
        /// Reuse is always by the SAP CardCode mapping, never by name, so an unrelated customer of the other company is never touched.
        /// </summary>
        public string Ensure(SapPartner bp, SyncOutcome outcome)
        {
            var map = _store.GetCustomerMap(bp.CardCode);
            if (map != null && !string.IsNullOrEmpty(map.ErpName))
            {
                if (_erp.Exists("Customer", map.ErpName)) return map.ErpName;
                Logger.Warn("customer.map.stale", "card", bp.CardCode, "erp", map.ErpName);
                _store.RemoveCustomerMap(bp.CardCode);
            }

            var found = _erp.GetList("Customer", new[] { "name" },
                new object[] { new object[] { "custom_sap_database", "=", _cfg.SapDatabase }, new object[] { "custom_sap_card_code", "=", bp.CardCode } }, 5);
            if (found.Count > 0)
            {
                string n = (string)found[0]["name"];
                _store.SaveCustomerMap(new MapEntry { SapKey = bp.CardCode, ErpName = n, Created = "N", Note = "linked by GST Portal custom field" });
                return n;
            }

            // address needed first to decide GST category / country
            var bill = _sap.GetAddress(bp.CardCode, bp.BillToDefault, "B");
            string country = (bill != null && !string.IsNullOrEmpty(bill.Country)) ? bill.Country : "India";
            bool overseas = !string.Equals(country, "India", StringComparison.OrdinalIgnoreCase);

            var doc = new JObject
            {
                ["doctype"] = "Customer",
                ["customer_name"] = Util.Trunc(Util.Safe(bp.CardName), 140),
                ["customer_type"] = "Company",
                ["customer_group"] = _lk.EnsureCustomerGroup(bp.GroupName, _cfg.DefaultCustomerGroup),
                ["territory"] = _lk.Territory(overseas),
                ["custom_sap_database"] = _cfg.SapDatabase,
                ["custom_sap_card_code"] = bp.CardCode,
                ["gst_category"] = overseas ? "Overseas" : (bill != null && !string.IsNullOrEmpty(bill.Gstin) ? "Registered Regular" : "Unregistered")
            };
            if (!string.IsNullOrEmpty(bp.Pan)) doc["pan"] = bp.Pan;
            string cur = _cfg.MapCurrency(bp.Currency);
            if (!string.IsNullOrEmpty(cur) && !string.Equals(cur, _lk.CompanyCurrency, StringComparison.OrdinalIgnoreCase) && cur.Length == 3 && !cur.Equals("AC", StringComparison.OrdinalIgnoreCase))
            {
                try { _lk.RequireEnabledCurrency(cur); doc["default_currency"] = cur; }
                catch (ConfigurationException) { outcome.Warnings.Add("Customer currency " + cur + " is not enabled in ERPNext; default currency not set."); }
            }
            if (!string.IsNullOrEmpty(bp.PaymentTerms) && _erp.Exists("Payment Terms Template", bp.PaymentTerms)) doc["payment_terms"] = bp.PaymentTerms;
            else if (!string.IsNullOrEmpty(bp.PaymentTerms)) outcome.Warnings.Add("Payment terms '" + bp.PaymentTerms + "' have no GST Portal Payment Terms Template; not set on customer.");

            var created = _erp.Insert("Customer", doc);
            string name = (string)created["name"];
            _store.SaveCustomerMap(new MapEntry { SapKey = bp.CardCode, ErpName = name, Created = "Y", Note = "created by add-on" });
            outcome.CustomerCreated = true;
            Logger.Info("customer.created", "card", bp.CardCode, "erp", name);

            TryAddress(name, bp, bill, "Billing", outcome);
            if (!string.IsNullOrEmpty(bp.ShipToDefault)) TryAddress(name, bp, _sap.GetAddress(bp.CardCode, bp.ShipToDefault, "S"), "Shipping", outcome);
            TryContact(name, bp, outcome);
            return name;
        }

        private void TryAddress(string customer, SapPartner bp, SapAddress a, string type, SyncOutcome outcome)
        {
            try { EnsureAddress(customer, bp, a, type, outcome); }
            catch (ErpUnavailableException) { throw; }
            catch (ErpAuthException) { throw; }
            catch (Exception ex) { outcome.Warnings.Add(type + " address for " + bp.CardCode + " not created: " + ex.Message); Logger.Warn("address.failed", "card", bp.CardCode, "error", ex.Message); }
        }

        /// <summary>Creates (once) the ERPNext Address for a SAP address and links it to the customer. Returns the ERPNext Address name or null.</summary>
        public string EnsureAddress(string customer, SapPartner bp, SapAddress a, string type, SyncOutcome outcome)
        {
            if (a == null) return null;
            string title = Util.Safe(bp.CardCode + "-" + a.Code);
            string name = title + "-" + type;
            if (_erp.Exists("Address", name)) return name;

            string line1 = string.Join(", ", new[] { a.Building, a.StreetNo, a.Street, a.Block }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrEmpty(line1)) line1 = a.Line2;
            if (string.IsNullOrEmpty(line1)) line1 = a.City;
            if (string.IsNullOrEmpty(line1)) { outcome.Warnings.Add(type + " address '" + a.Code + "' has no address text in SAP; not created."); return null; }

            var doc = new JObject
            {
                ["doctype"] = "Address",
                ["address_title"] = title,
                ["address_type"] = type,
                ["address_line1"] = Util.Trunc(line1, 140),
                ["city"] = string.IsNullOrWhiteSpace(a.City) ? "-" : a.City,
                ["country"] = string.IsNullOrEmpty(a.Country) ? "India" : a.Country,
                ["links"] = Util.Arr(new JObject { ["link_doctype"] = "Customer", ["link_name"] = customer })
            };
            if (!string.IsNullOrEmpty(a.Line2) && a.Line2 != line1) doc["address_line2"] = Util.Trunc(a.Line2, 140);
            if (!string.IsNullOrEmpty(a.StateName)) doc["state"] = a.StateName;
            if (!string.IsNullOrEmpty(a.ZipCode)) doc["pincode"] = a.ZipCode;
            bool overseas = !string.Equals((string)doc["country"], "India", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(a.Gstin)) { doc["gstin"] = a.Gstin; doc["gst_category"] = "Registered Regular"; }
            else doc["gst_category"] = overseas ? "Overseas" : "Unregistered";
            if (type == "Billing") doc["is_primary_address"] = 1; else doc["is_shipping_address"] = 1;
            _erp.Insert("Address", doc);
            Logger.Info("address.created", "name", name);
            return name;
        }

        private void TryContact(string customer, SapPartner bp, SyncOutcome outcome)
        {
            if (string.IsNullOrEmpty(bp.Email) && string.IsNullOrEmpty(bp.Phone1) && string.IsNullOrEmpty(bp.Cellular)) return;
            try
            {
                string first = Util.Trunc(Util.Safe(string.IsNullOrEmpty(bp.ContactPerson) ? bp.CardName : bp.ContactPerson), 100);
                var doc = new JObject { ["doctype"] = "Contact", ["first_name"] = first, ["links"] = Util.Arr(new JObject { ["link_doctype"] = "Customer", ["link_name"] = customer }) };
                if (!string.IsNullOrEmpty(bp.Email)) doc["email_ids"] = Util.Arr(new JObject { ["email_id"] = bp.Email, ["is_primary"] = 1 });
                var phones = new List<JObject>();
                if (!string.IsNullOrEmpty(bp.Phone1)) phones.Add(new JObject { ["phone"] = bp.Phone1, ["is_primary_phone"] = 1 });
                if (!string.IsNullOrEmpty(bp.Cellular)) phones.Add(new JObject { ["phone"] = bp.Cellular, ["is_primary_mobile_no"] = 1 });
                if (phones.Count > 0) doc["phone_nos"] = new JArray(phones);
                _erp.Insert("Contact", doc);
            }
            catch (ErpUnavailableException) { throw; }
            catch (ErpAuthException) { throw; }
            catch (Exception ex) { outcome.Warnings.Add("Contact for " + bp.CardCode + " not created: " + ex.Message); }
        }
    }

    public sealed class ItemSync
    {
        private readonly ErpClient _erp; private readonly IStateStore _store; private readonly ISapSource _sap; private readonly ErpLookups _lk; private readonly WarehouseSync _whs; private readonly AppConfig _cfg;
        public ItemSync(ErpClient erp, IStateStore store, ISapSource sap, ErpLookups lk, WarehouseSync whs) { _erp = erp; _store = store; _sap = sap; _lk = lk; _whs = whs; _cfg = erp.Config; }

        /// <summary>Returns the ERPNext item code (item_code = SAP ItemCode). Existing items are reused, verified and never modified.</summary>
        public string Ensure(string sapItemCode, SyncOutcome outcome)
        {
            var existing = _erp.GetDoc("Item", sapItemCode);
            if (existing != null)
            {
                VerifyReusable(existing, sapItemCode);
                string erpCode = (string)existing["name"];
                var m = _store.GetItemMap(sapItemCode);
                if (m == null || m.ErpName != erpCode)
                {
                    bool ours = (string)existing["custom_sap_database"] == _cfg.SapDatabase;
                    _store.SaveItemMap(new MapEntry { SapKey = sapItemCode, ErpName = erpCode, Created = "N", Note = ours ? "linked (SAP reference present)" : "pre-existing GST Portal item reused, not modified" });
                    if (!ours) outcome.Warnings.Add("Item '" + sapItemCode + "' already existed in GST Portal without SAP reference; reused unchanged.");
                }
                if (string.IsNullOrEmpty((string)existing["gst_hsn_code"]))
                {
                    // GST Portal (GST Settings: validate HSN) rejects invoices for items without HSN/SAC.
                    bool ours = (string)existing["custom_sap_database"] == _cfg.SapDatabase;
                    var sapItem = _sap.GetItem(sapItemCode);
                    string hsn = ResolveHsn(sapItem, outcome);
                    if (hsn == null || !ours)
                        throw new ConfigurationException("Item '" + sapItemCode + "' has no HSN/SAC in GST Portal" + (hsn == null ? " and SAP has no usable HSN/SAC for it (SAP: '" + (string.IsNullOrEmpty(sapItem.Hsn) ? sapItem.Sac : sapItem.Hsn) + "')." : "; it was not created by this add-on so it is not modified - set the HSN in GST Portal (suggested " + hsn + ")."));
                    _erp.Update("Item", erpCode, new JObject { ["gst_hsn_code"] = hsn });      // only the add-on's own item, only this field
                    Logger.Info("item.hsn.updated", "item", erpCode, "hsn", hsn);
                }
                return erpCode;
            }

            var it = _sap.GetItem(sapItemCode);
            string stockUom = _lk.EnsureUom(string.IsNullOrWhiteSpace(it.InventoryUom) ? it.SalesUom : it.InventoryUom);
            var doc = new JObject
            {
                ["doctype"] = "Item",
                ["item_code"] = Util.Trunc(it.ItemCode, 140),
                ["item_name"] = Util.Trunc(string.IsNullOrWhiteSpace(it.ItemName) ? it.ItemCode : it.ItemName, 140),
                ["description"] = string.IsNullOrWhiteSpace(it.ForeignName) ? it.ItemName : it.ForeignName,
                ["item_group"] = _lk.EnsureItemGroup(it.GroupName),
                ["stock_uom"] = stockUom,
                ["is_stock_item"] = it.Inventory ? 1 : 0,
                ["valuation_method"] = _cfg.DefaultValuationMethod,
                ["is_sales_item"] = 1,
                ["is_purchase_item"] = it.Purchase ? 1 : 0,
                ["custom_sap_database"] = _cfg.SapDatabase,
                ["custom_sap_item_code"] = it.ItemCode
            };
            if (it.ItemCode.Length > 140) throw new ConfigurationException("SAP item code '" + it.ItemCode + "' is longer than GST Portal's 140 character limit.");

            string hsnCode = ResolveHsn(it, outcome);
            if (hsnCode == null)
                throw new ConfigurationException("Item '" + it.ItemCode + "' has no usable HSN/SAC (SAP value '" + (string.IsNullOrEmpty(it.Hsn) ? it.Sac : it.Hsn) + "' is empty or not in GST Portal's GST HSN Code list). GST Portal requires one on every invoice item; set the HSN/SAC on the item in SAP. Nothing was created.");
            doc["gst_hsn_code"] = hsnCode;

            if (!string.IsNullOrEmpty(it.DefaultWarehouse))
            {
                try
                {
                    string w = _whs.Ensure(it.DefaultWarehouse, outcome);
                    doc["item_defaults"] = Util.Arr(new JObject { ["company"] = _cfg.ErpCompany, ["default_warehouse"] = w });
                }
                catch (ConfigurationException ex) { outcome.Warnings.Add("Default warehouse of item " + it.ItemCode + " not set: " + ex.Message); }
            }

            var created = _erp.Insert("Item", doc);
            string code = (string)created["name"];
            _store.SaveItemMap(new MapEntry { SapKey = sapItemCode, ErpName = code, Created = "Y", Note = "created by add-on" });
            outcome.ItemsCreated++;
            Logger.Info("item.created", "item", code);
            return code;
        }

        /// <summary>
        /// Finds the HSN/SAC code in ERPNext's GST HSN Code list: the SAP code itself, else its 8- or 6-digit prefix
        /// (a real parent code, never an invented one). Returns null if none exists.
        /// </summary>
        private string ResolveHsn(SapItem it, SyncOutcome outcome)
        {
            string code = !string.IsNullOrEmpty(it.Hsn) ? it.Hsn : it.Sac;
            if (string.IsNullOrEmpty(code)) return null;
            var candidates = new List<string> { code };
            if (code.Length > 8) candidates.Add(code.Substring(0, 8));
            if (code.Length > 6) { candidates.Add(code.Substring(0, 6) + "00"); candidates.Add(code.Substring(0, 6)); }     // 6-digit heading written as 8 digits (e.g. 7606.12.20 -> 76061200)
            foreach (var c in candidates.Distinct())
            {
                if (!_lk.HsnExists(c)) continue;
                if (c != code) outcome.Warnings.Add("HSN " + code + " of item " + it.ItemCode + " is not in ERPNext; used its parent code " + c + ".");
                return c;
            }
            return null;
        }

        private static void VerifyReusable(JObject item, string code)
        {
            if ((int)(item["disabled"] ?? 0) == 1) throw new ConfigurationException("GST Portal item '" + code + "' is disabled. Enable it or map the SAP item to another GST Portal item.");
            if ((int)(item["is_sales_item"] ?? 1) == 0) throw new ConfigurationException("GST Portal item '" + code + "' is not marked as a Sales Item. Enable 'Allow to be sold' on it in GST Portal.");
        }
    }

    public sealed class WarehouseSync
    {
        private readonly ErpClient _erp; private readonly IStateStore _store; private readonly ISapSource _sap; private readonly AppConfig _cfg;
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ErpLookups _lk;
        public WarehouseSync(ErpClient erp, IStateStore store, ISapSource sap, ErpLookups lk = null) { _erp = erp; _store = store; _sap = sap; _cfg = erp.Config; _lk = lk ?? new ErpLookups(erp); }

        /// <summary>Resolves a SAP warehouse to an ERPNext warehouse owned by the configured company; creates it under that company if needed.</summary>
        public string Ensure(string sapWhsCode, SyncOutcome outcome)
        {
            string c;
            if (_cache.TryGetValue(sapWhsCode, out c)) return c;
            var map = _store.GetWarehouseMap(sapWhsCode);
            if (map != null && !string.IsNullOrEmpty(map.ErpName))
            {
                c = Validate(map.ErpName, sapWhsCode);     // throws if missing/other company/group/disabled
                _cache[sapWhsCode] = c;
                return c;
            }

            var w = _sap.GetWarehouse(sapWhsCode);
            string baseName = Util.Safe(w.Code);
            string candidate = baseName + " - " + _cfg.ErpAbbr;
            var d = _erp.GetDoc("Warehouse", candidate);
            if (d != null)
            {
                c = Validate(candidate, sapWhsCode);
                _store.SaveWarehouseMap(new MapEntry { SapKey = sapWhsCode, ErpName = c, Created = "N", Note = "matched by code" });
            }
            else
            {
                var doc = new JObject
                {
                    ["doctype"] = "Warehouse", ["warehouse_name"] = baseName, ["company"] = _cfg.ErpCompany,
                    ["parent_warehouse"] = _lk.RootWarehouse(), ["is_group"] = 0
                };
                if (!string.IsNullOrWhiteSpace(w.City)) doc["city"] = w.City;
                if (!string.IsNullOrWhiteSpace(w.ZipCode)) doc["pin"] = w.ZipCode;
                if (!string.IsNullOrWhiteSpace(w.Street)) doc["address_line_1"] = Util.Trunc(w.Street, 140);
                var created = _erp.Insert("Warehouse", doc);
                c = (string)created["name"];
                _store.SaveWarehouseMap(new MapEntry { SapKey = sapWhsCode, ErpName = c, Created = "Y", Note = "created by add-on for " + w.Name });
                Logger.Info("warehouse.created", "sap", sapWhsCode, "erp", c);
            }
            _cache[sapWhsCode] = c;
            return c;
        }

        /// <summary>Ownership check: the warehouse must exist, be a leaf, be enabled and belong to the configured company.</summary>
        public string Validate(string erpWarehouse, string sapCode)
        {
            var d = _erp.GetDoc("Warehouse", erpWarehouse);
            if (d == null) throw new ConfigurationException("Mapped GST Portal warehouse '" + erpWarehouse + "' (SAP " + sapCode + ") does not exist.");
            string owner = (string)d["company"];
            if (!string.Equals(owner, _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Warehouse '" + erpWarehouse + "' (mapped from SAP " + sapCode + ") belongs to company '" + owner + "', not '" + _cfg.ErpCompany + "'. It will not be used.");
            if ((int)(d["is_group"] ?? 0) == 1) throw new ConfigurationException("Warehouse '" + erpWarehouse + "' is a group warehouse; map to a leaf warehouse.");
            if ((int)(d["disabled"] ?? 0) == 1) throw new ConfigurationException("Warehouse '" + erpWarehouse + "' is disabled.");
            return (string)d["name"];
        }
    }
}
