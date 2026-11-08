using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.Tests
{
    /// <summary>Tiny in-memory emulation of the Frappe REST resource API (just enough for the sync engine).</summary>
    public sealed class FakeErp : HttpMessageHandler
    {
        public readonly Dictionary<string, Dictionary<string, JObject>> Docs = new Dictionary<string, Dictionary<string, JObject>>(StringComparer.OrdinalIgnoreCase);
        public bool Down;
        public bool FailAfterInsertOfSalesInvoice;      // simulates a lost response: document is saved, caller sees a timeout
        public readonly List<string> Calls = new List<string>();
        public string LastAuth;
        private int _seq;

        public FakeErp()
        {
            Seed("Currency", "INR", new JObject { ["enabled"] = 1 });
            Seed("Currency", "EUR", new JObject { ["enabled"] = 1 });
            Seed("Account", "Output Tax CGST - VCPL", new JObject { ["company"] = "Vipra Closures Private Limited" });
            Seed("Account", "Output Tax SGST - VCPL", new JObject { ["company"] = "Vipra Closures Private Limited" });
            Seed("Account", "Output Tax IGST - VCPL", new JObject { ["company"] = "Vipra Closures Private Limited" });
            Seed("Account", "Rounded Off - VCPL", new JObject { ["company"] = "Vipra Closures Private Limited" });
            Seed("Account", "Output Tax IGST - TTCPL", new JObject { ["company"] = "Tint Tech Coatings Private Limited" });
            Seed("Customer Group", "Commercial", new JObject());
            Seed("Item Group", "Products", new JObject());
            Seed("UOM", "Nos", new JObject());
            Seed("UOM", "Kg", new JObject());
            Seed("Warehouse", "All Warehouses - VCPL", new JObject { ["company"] = "Vipra Closures Private Limited", ["is_group"] = 1 });
            Seed("Warehouse", "Stores - TTCPL", new JObject { ["company"] = "Tint Tech Coatings Private Limited", ["is_group"] = 0 });
        }

        public void Seed(string dt, string name, JObject d)
        {
            if (!Docs.ContainsKey(dt)) Docs[dt] = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            d["name"] = name; Docs[dt][name] = d;
        }

        public IEnumerable<JObject> All(string dt) { return Docs.ContainsKey(dt) ? Docs[dt].Values : Enumerable.Empty<JObject>(); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastAuth = req.Headers.TryGetValues("Authorization", out var v) ? v.First() : null;
            Calls.Add(req.Method + " " + req.RequestUri.AbsolutePath);
            if (Down) throw new HttpRequestException("connection refused (simulated)");
            string path = Uri.UnescapeDataString(req.RequestUri.AbsolutePath);
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);   // api resource <dt> [name]
            string dt = parts.Length > 2 ? parts[2] : null, name = parts.Length > 3 ? parts[3] : null;

            if (LastAuth == null || !LastAuth.StartsWith("token ")) return Reply(401, "{\"exc_type\":\"AuthenticationError\"}");
            if (req.Method == HttpMethod.Get && name == null)
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
                var filters = q["filters"] == null ? new JArray() : JArray.Parse(q["filters"]);
                var rows = All(dt).Where(d => filters.All(f => Match(d, (JArray)f))).Select(d => (JObject)d.DeepClone()).ToList();
                return Reply(200, new JObject { ["data"] = new JArray(rows) }.ToString());
            }
            if (req.Method == HttpMethod.Get)
            {
                if (!Docs.ContainsKey(dt) || !Docs[dt].ContainsKey(name)) return Reply(404, "{\"exc_type\":\"DoesNotExistError\"}");
                return Reply(200, new JObject { ["data"] = Docs[dt][name] }.ToString());
            }
            var body = JObject.Parse(req.Content.ReadAsStringAsync().Result);
            if (req.Method == HttpMethod.Post)
            {
                string nm = (string)body["customer_name"] ?? (string)body["item_code"] ?? (string)body["uom_name"] ?? (string)body["customer_group_name"] ?? (string)body["item_group_name"];
                if (dt == "Sales Invoice") nm = "ACC-SINV-2026-" + (++_seq).ToString("00000");
                else if (dt == "Warehouse") nm = (string)body["warehouse_name"] + " - VCPL";
                else if (dt == "Address") nm = (string)body["address_title"] + "-" + (string)body["address_type"];
                else if (dt == "Contact") nm = (string)body["first_name"] + "-C";
                else if (dt == "Custom Field") nm = (string)body["dt"] + "-" + (string)body["fieldname"];
                else if (nm == null) nm = dt + "-" + (++_seq);
                if (Docs.ContainsKey(dt) && Docs[dt].ContainsKey(nm)) nm = nm + "-1";
                if (dt == "Sales Invoice")
                {
                    decimal net = body["items"].Sum(i => (decimal)i["qty"] * (decimal)i["price_list_rate"] * (1 - (decimal)i["discount_percentage"] / 100m));
                    decimal tax = body["taxes"].Sum(t => (decimal)t["tax_amount"]);
                    body["grand_total"] = net + tax; body["rounded_total"] = Math.Round(net + tax, 0); body["creation"] = "2026-11-08 10:00:00";
                }
                Seed(dt, nm, body);
                if (dt == "Sales Invoice" && FailAfterInsertOfSalesInvoice) { FailAfterInsertOfSalesInvoice = false; throw new TaskCanceledException("timeout (simulated)"); }
                return Reply(200, new JObject { ["data"] = Docs[dt][nm] }.ToString());
            }
            if (req.Method == HttpMethod.Put) { foreach (var p in body.Properties()) Docs[dt][name][p.Name] = p.Value; return Reply(200, new JObject { ["data"] = Docs[dt][name] }.ToString()); }
            return Reply(405, "{}");
        }

        private static bool Match(JObject d, JArray f)
        {
            var v = d[(string)f[0]];
            return v != null && string.Equals(v.ToString(), f[2].ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static Task<HttpResponseMessage> Reply(int code, string json)
        {
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    public sealed class FakeSap : ISapSource
    {
        public string DatabaseName { get { return "Vipra"; } }
        public readonly Dictionary<int, SapInvoice> Invoices = new Dictionary<int, SapInvoice>();
        public readonly Dictionary<string, SapPartner> Partners = new Dictionary<string, SapPartner>();
        public readonly Dictionary<string, SapItem> Items = new Dictionary<string, SapItem>();
        public readonly Dictionary<string, SapWarehouse> Whs = new Dictionary<string, SapWarehouse>();

        public SapInvoice GetInvoice(int d) { return Invoices[d]; }
        public SapPartner GetPartner(string c) { return Partners[c]; }
        public SapAddress GetAddress(string c, string a, string t)
        {
            return string.IsNullOrEmpty(a) ? null : new SapAddress { Code = a, Type = t, Street = "1 Test Road", City = "Thane", ZipCode = "400601", StateName = "Maharashtra", Country = "India" };
        }
        public SapItem GetItem(string c) { return Items[c]; }
        public SapWarehouse GetWarehouse(string c) { return Whs[c]; }
        public List<SapWarehouse> ListWarehouses() { return Whs.Values.ToList(); }
        public int GetMaxDocEntry() { return Invoices.Keys.DefaultIfEmpty(0).Max(); }
        public List<int> ListUnrecordedInvoices(int from) { return Invoices.Keys.Where(k => k >= from).ToList(); }

        public static FakeSap WithInvoices(params int[] docEntries)
        {
            var s = new FakeSap();
            s.Partners["C1"] = new SapPartner { CardCode = "C1", CardName = "Test Customer One", GroupName = "Commercial", Currency = "`", BillToDefault = "HQ", ShipToDefault = "HQ" };
            s.Items["ITEM-A"] = new SapItem { ItemCode = "ITEM-A", ItemName = "Item A", GroupName = "Products", Inventory = true, Sales = true, InventoryUom = "Kg", Hsn = "" };
            s.Items["ITEM-B"] = new SapItem { ItemCode = "ITEM-B", ItemName = "Item B", GroupName = "Products", Inventory = true, Sales = true, InventoryUom = "Nos", DefaultWarehouse = "W1" };
            s.Whs["W1"] = new SapWarehouse { Code = "W1", Name = "Main", City = "Thane" };
            foreach (var d in docEntries)
            {
                var inv = new SapInvoice
                {
                    DocEntry = d, DocNum = 1000 + d, Database = "Vipra", DocType = "I", CardCode = "C1", CardName = "Test Customer One",
                    PostingDate = new DateTime(2026, 11, 3), DocumentDate = new DateTime(2026, 11, 3), DueDate = new DateTime(2026, 12, 3), Currency = "`", PayToCode = "HQ", ShipToCode = "HQ",
                    DocTotal = 1180m
                };
                inv.Lines.Add(new SapInvoiceLine { LineNum = 0, ItemCode = "ITEM-A", ItemName = "Item A", Uom = "Kg", Quantity = 10, UnitPrice = 100, LineTotal = 1000, WarehouseCode = "W1", TaxCode = "GST@18" });
                inv.Taxes.Add(new SapTaxAmount { LineNum = 0, TaxType = "-100", TaxCode = "GST@18", Rate = 9, Amount = 90, RelateType = 1 });
                inv.Taxes.Add(new SapTaxAmount { LineNum = 0, TaxType = "-110", TaxCode = "GST@18", Rate = 9, Amount = 90, RelateType = 1 });
                s.Invoices[d] = inv;
            }
            return s;
        }
    }
}
