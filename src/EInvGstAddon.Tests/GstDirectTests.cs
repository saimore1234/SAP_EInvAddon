using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EInvGstAddon.Tests
{
    /// <summary>Scripted stand-in for the ERPNext GST API. Records every request so tests can prove what was (not) sent.</summary>
    public sealed class FakeGstApi : HttpMessageHandler
    {
        public readonly List<string> Paths = new List<string>();
        public readonly List<JObject> Bodies = new List<JObject>();
        public readonly List<string> Auth = new List<string>();
        public readonly Dictionary<string, Func<JObject, object>> Handlers = new Dictionary<string, Func<JObject, object>>();
        public bool Down; public HttpStatusCode? Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Paths.Add(req.Method + " " + req.RequestUri.AbsolutePath);
            Auth.Add(req.Headers.TryGetValues("Authorization", out var v) ? v.First() : null);
            if (Down) throw new HttpRequestException("connection refused (simulated)");
            if (Fail.HasValue) return Task.FromResult(new HttpResponseMessage(Fail.Value) { Content = new StringContent("{\"exc_type\":\"PermissionError\",\"exception\":\"denied\"}") });
            string ep = req.RequestUri.AbsolutePath.Substring(req.RequestUri.AbsolutePath.LastIndexOf('.') + 1);
            JObject body = req.Content == null ? new JObject() : JObject.Parse(req.Content.ReadAsStringAsync().Result);
            Bodies.Add(body);
            object result = Handlers.ContainsKey(ep) ? Handlers[ep](body) : new { ok = true, status = "Not generated", request_id = (string)body["request_id"], reference = "", message = "", data = new { }, errors = new object[0], warnings = new object[0] };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JObject { ["message"] = JToken.FromObject(result) }.ToString(), Encoding.UTF8, "application/json") });
        }
    }

    public class GstGatewayTests
    {
        private static (GstGateway g, FakeGstApi api, AppConfig cfg) Make(string iid = "IT1", string db = "DB_ONE", string env = "Sandbox")
        {
            var cfg = new AppConfig { Id = "c1", IntegrationId = iid, SapDatabase = db, ErpUrl = "https://erp.example.test", ApiKey = "key123456", Environment = env };
            cfg.ApiSecret = "secret123456";
            var api = new FakeGstApi();
            return (new GstGateway(new ErpClient(cfg, api)), api, cfg);
        }

        private static JObject Inv() { return new JObject { ["document_number"] = "INV/1", ["items"] = new JArray() }; }

        [Fact]
        public void Request_uses_https_token_auth_the_documented_endpoint_and_the_configuration_identity()
        {
            var t = Make();
            t.g.Validate(2624, Inv());
            Assert.Equal("POST /api/method/sap_b1_direct_integration.api.gst.validate_invoice", t.api.Paths.Single());
            Assert.Equal("token key123456:secret123456", t.api.Auth.Single());
            var b = t.api.Bodies.Single();
            Assert.Equal("IT1", (string)b["integration_id"]); Assert.Equal("DB_ONE", (string)b["sap_database"]); Assert.Equal("13", (string)b["object_type"]);
            Assert.Equal(2624, (int)b["doc_entry"]); Assert.Equal("Sandbox", (string)b["environment"]); Assert.Equal("INV/1", (string)b["invoice"]["document_number"]);
            Assert.Matches("^[0-9a-f]{32}$", (string)b["request_id"]);
        }

        [Fact]
        public void Http_is_required()
        {
            var cfg = new AppConfig { IntegrationId = "IT1", SapDatabase = "D", ErpUrl = "http://erp.example.test", ApiKey = "k12345", Environment = "Sandbox" }; cfg.ApiSecret = "s123456";
            Assert.Throws<ConfigurationException>(() => new GstGateway(new ErpClient(cfg, new FakeGstApi())).Validate(1, Inv()));
        }

        [Fact]
        public void Every_operation_uses_only_the_gst_api_and_never_the_resource_api()
        {
            var t = Make();
            t.g.Validate(1, Inv()); t.g.GenerateEInvoice(1, Inv()); t.g.EInvoiceStatus(1); t.g.CancelEInvoice(1, "Duplicate", "x");
            t.g.GenerateEWayBill(1, Inv()); t.g.EWayBillStatus(1); t.g.CancelEWayBill(1, "Duplicate", "x"); t.g.UpdateVehicle(1, "MH04AB1234", "Others", "x", "Thane", "27"); t.g.UpdateTransporter(1, "27AAACT9999A1Z5");
            Assert.Equal(9, t.api.Paths.Count);
            Assert.All(t.api.Paths, p => Assert.StartsWith("POST /api/method/sap_b1_direct_integration.api.gst.", p));
            Assert.DoesNotContain(t.api.Paths, p => p.Contains("/api/resource"));              // no Sales Invoice / Customer / Item / Warehouse ever
        }

        [Fact]
        public void The_server_answer_is_returned_exactly_and_success_is_never_invented()
        {
            var t = Make();
            t.api.Handlers["generate_e_invoice"] = b => new { ok = false, status = "Failed", request_id = "r", reference = "R", message = "2172: invalid", data = new { irn = "SHOULD-NOT-COUNT" }, errors = new[] { new { severity = "error", code = "X", field = "f", message = "bad" } }, warnings = new object[0] };
            var r = t.g.GenerateEInvoice(1, Inv());
            Assert.False(r.Ok); Assert.Equal("Failed", r.Status); Assert.Single(r.Errors); Assert.Contains("bad", r.Describe());
            t.api.Handlers["generate_e_invoice"] = b => new { ok = true, status = "Generated", request_id = "r", reference = "R", message = "ok", data = new { irn = new string('a', 64), ack_no = "1122", ack_date = "2026-11-09 10:00:00", signed_qr = "QR", signed_invoice = "SI" }, errors = new object[0], warnings = new object[0] };
            var ok = t.g.GenerateEInvoice(1, Inv());
            Assert.True(ok.Ok); Assert.Equal(64, ok.S("irn").Length); Assert.Equal("QR", ok.S("signed_qr"));
        }

        [Fact]
        public void Status_combines_einvoice_and_ewaybill_state_without_inventing_any_document()
        {
            var t = Make();
            t.api.Handlers["get_e_invoice_status"] = b => new { ok = true, status = "Generated", request_id = "r", reference = "R", message = "", data = new { irn = "IRN64", ack_no = "9", ack_date = "2026-11-09 10:00:00", signed_qr = "QR", signed_invoice = "SIGNED" }, errors = new object[0], warnings = new object[0] };
            t.api.Handlers["get_e_waybill_status"] = b => new { ok = true, status = "Not generated", request_id = "r", reference = "", message = "No e-Way Bill", data = new { }, errors = new object[0], warnings = new object[0] };
            var s = t.g.Status(2624, 67);
            Assert.True(s.HasIrn); Assert.Equal("IRN64", s.Irn); Assert.Equal("9", s.AckNo); Assert.NotNull(s.AckDate); Assert.False(s.HasEwb); Assert.Equal("Not generated", s.EwbStatus);
            Assert.Equal(1, s.DocStatus);                                          // no ERPNext document exists: always "ready"
        }

        [Fact]
        public void Unknown_outcome_is_surfaced_and_reconciliation_sends_the_invoice()
        {
            var t = Make();
            t.api.Handlers["get_e_invoice_status"] = b => new { ok = false, status = "Unknown / Reconciliation Required", request_id = "r", reference = "R", message = "needs invoice", data = new { }, errors = new object[0], warnings = new object[0] };
            var s = t.g.Status(1, 1, true, Inv());
            Assert.True(s.EInvoiceUnknown); Assert.False(s.HasIrn);
            Assert.NotNull(t.api.Bodies.First()["invoice"]);                       // identical invoice data is supplied for the server-side reconciliation
            var rec = t.g.ToRecord(1, 1, s);
            Assert.Equal("Unknown", rec.EInvStatus); Assert.StartsWith("UNKNOWN", rec.Error);
        }

        [Fact]
        public void Cancelled_irn_is_reported_as_cancelled()
        {
            var t = Make();
            t.api.Handlers["get_e_invoice_status"] = b => new { ok = true, status = "Cancelled", request_id = "r", reference = "R", message = "", data = new { ack_no = "9" }, errors = new object[0], warnings = new object[0] };
            var s = t.g.Status(1, 1);
            Assert.True(s.IrnCancelled); Assert.False(s.HasIrn);
            Assert.Equal("Cancelled", t.g.ToRecord(1, 1, s).EInvStatus);
        }

        [Fact]
        public void Authorisation_errors_and_outages_are_distinguished()
        {
            var t = Make(); t.api.Fail = HttpStatusCode.Forbidden;
            Assert.Throws<ErpAuthException>(() => t.g.Validate(1, Inv()));
            var u = Make(); u.api.Down = true;
            Assert.Throws<ErpUnavailableException>(() => u.g.GenerateEInvoice(1, Inv()));      // uncertain: the caller must reconcile, never blindly retry
        }

        [Fact]
        public void Two_configurations_send_their_own_identity_and_never_mix()
        {
            var a = Make("IT_A", "DB_A", "Sandbox"); var b = Make("IT_B", "DB_B", "Production");
            a.g.GenerateEInvoice(1, Inv()); b.g.GenerateEInvoice(1, Inv());
            Assert.Equal(("IT_A", "DB_A", "Sandbox"), ((string)a.api.Bodies[0]["integration_id"], (string)a.api.Bodies[0]["sap_database"], (string)a.api.Bodies[0]["environment"]));
            Assert.Equal(("IT_B", "DB_B", "Production"), ((string)b.api.Bodies[0]["integration_id"], (string)b.api.Bodies[0]["sap_database"], (string)b.api.Bodies[0]["environment"]));
            Assert.NotEqual((string)a.api.Bodies[0]["request_id"], (string)b.api.Bodies[0]["request_id"]);
        }

        [Fact]
        public void A_configuration_without_an_integration_id_cannot_call_the_api()
        {
            var t = Make(iid: "");
            Assert.Throws<ConfigurationException>(() => t.g.GenerateEInvoice(1, Inv()));
            Assert.Empty(t.api.Paths);
        }

        [Fact]
        public void Api_secret_is_never_written_to_the_log()
        {
            string dir = Path.Combine(Path.GetTempPath(), "einv-log-" + Guid.NewGuid().ToString("N"));
            Logger.Directory = dir;
            var t = Make(); Logger.RegisterSecret("secret123456"); Logger.RegisterSecret("key123456");
            t.g.Validate(1, Inv());
            Logger.Warn("probe", "auth", t.api.Auth.Single());
            string all = string.Join("\n", Directory.GetFiles(dir).Select(File.ReadAllText));
            Assert.DoesNotContain("secret123456", all); Assert.DoesNotContain("key123456", all);
            Logger.Directory = null;
        }
    }

    public class InvoiceJsonBuilderTests
    {
        public static AppConfig Cfg()
        {
            var c = new AppConfig { IntegrationId = "IT1", SapDatabase = "DB_ONE", SupplierGstin = "27AABCT1234F1ZX", LegalName = "Seller Ltd", GstAddress = "1 Factory Road", GstCity = "Thane", StateCode = "27", GstPin = "400601", Environment = "Sandbox" };
            c.CurrencyMap["`"] = "INR";
            return c;
        }

        public static GstSource Src(bool igst = true)
        {
            var s = new GstSource
            {
                DocEntry = 2624, DocNum = 67, Series = 9, SeriesName = "EX2627", Database = "DB_ONE", DocType = "I", CardCode = "C1", CardName = "Buyer Pvt Ltd", PostingDate = new DateTime(2026, 11, 8), DueDate = new DateTime(2026, 12, 8),
                DocCurrency = "`", DocTotalLc = 59000, RoundOffLc = 0, ShipStateCode = "KA",
                Buyer = new GstParty { Code = "C1", Name = "Buyer Pvt Ltd", Street = "2 Market Street", City = "Bengaluru", ZipCode = "560001", StateCode = "KA", CountryIso2 = "IN", Gstin = "29AAACB5678K1ZE" }
            };
            s.Lines.Add(new GstLine { LineNum = 0, ItemCode = "I1", Description = "Closures", Uom = "Kgs", Hsn = "39235010", Quantity = 100, UnitPrice = 500, LineTotalLc = 50000 });
            if (igst) s.Taxes.Add(new GstTaxRow { LineNum = 0, RelateType = 1, TaxType = "-120", TaxCode = "IGST@18", Rate = 18, AmountLc = 9000, BaseLc = 50000 });
            else { s.Taxes.Add(new GstTaxRow { LineNum = 0, RelateType = 1, TaxType = "-100", Rate = 9, AmountLc = 4500, BaseLc = 50000 }); s.Taxes.Add(new GstTaxRow { LineNum = 0, RelateType = 1, TaxType = "-110", Rate = 9, AmountLc = 4500, BaseLc = 50000 }); }
            return s;
        }

        public static Newtonsoft.Json.Linq.JToken TransportJson(TransportInput t) { var r = InvoiceJsonBuilder.Build(Src(), Cfg(), t); Assert.True(r.Ok, string.Join(";", r.Errors)); return r.Json["transport"]; }

        [Fact]
        public void Domestic_igst_invoice_maps_to_the_contract_without_inventing_values()
        {
            var r = InvoiceJsonBuilder.Build(Src(), Cfg());
            Assert.True(r.Ok, string.Join(";", r.Errors));
            var j = r.Json;
            Assert.Equal("IT1", (string)j["integration_id"]); Assert.Equal("13", (string)j["object_type"]); Assert.Equal(2624, (int)j["doc_entry"]); Assert.Equal("67", (string)j["document_number"]);
            Assert.Equal("B2B", (string)j["supply_type"]); Assert.Equal("29", (string)j["buyer"]["state_code"]); Assert.Equal("29", (string)j["place_of_supply"]);
            Assert.Equal("27AABCT1234F1ZX", (string)j["seller"]["gstin"]); Assert.Equal("Thane", (string)j["seller"]["city"]);
            var it = j["items"][0];
            Assert.Equal("39235010", (string)it["hsn_code"]); Assert.Equal("KGS", (string)it["unit"]); Assert.Equal(50000m, (decimal)it["taxable_value"]); Assert.Equal(18m, (decimal)it["igst_rate"]); Assert.Equal(9000m, (decimal)it["igst_amount"]);
            var t = j["tax_totals"];
            Assert.Equal(50000m, (decimal)t["taxable_value"]); Assert.Equal(9000m, (decimal)t["igst"]); Assert.Equal(59000m, (decimal)t["invoice_total"]);
        }

        [Fact]
        public void Cgst_sgst_split_freight_round_off_and_document_number_pattern()
        {
            var c = Cfg(); c.DocNumberPattern = "{SeriesName}/{DocNum}";
            var s = Src(false); s.Buyer.Gstin = "27AAACD1111A1ZB"; s.ShipStateCode = "MH"; s.Buyer.StateCode = "MH";
            s.Expenses.Add(new GstExpense { Name = "Packing", AmountLc = 200 }); s.RoundOffLc = -0.5m; s.DocTotalLc = 59199.5m;
            var r = InvoiceJsonBuilder.Build(s, c);
            Assert.True(r.Ok, string.Join(";", r.Errors));
            Assert.Equal("EX2627/67", (string)r.Json["document_number"]);
            Assert.Equal(4500m, (decimal)r.Json["tax_totals"]["cgst"]); Assert.Equal(4500m, (decimal)r.Json["tax_totals"]["sgst"]); Assert.Equal(0m, (decimal)r.Json["tax_totals"]["igst"]);
            Assert.Equal(200m, (decimal)r.Json["tax_totals"]["other_charges"]); Assert.Equal(-0.5m, (decimal)r.Json["tax_totals"]["round_off"]);
            Assert.Equal("27", (string)r.Json["place_of_supply"]);
        }

        [Fact]
        public void Unmapped_non_zero_sap_tax_stops_the_build_and_other_charge_mapping_includes_it()
        {
            var s = Src(); s.Taxes.Add(new GstTaxRow { LineNum = 0, RelateType = 1, TaxType = "7", TaxCode = "TCS", Rate = 0.075m, AmountLc = 44 });
            var r = InvoiceJsonBuilder.Build(s, Cfg());
            Assert.False(r.Ok); Assert.Contains("7 (TCS)", string.Join(" ", r.Errors));
            var c = Cfg(); c.OtherChargeTaxTypes.Add("7"); s.DocTotalLc = 59044;
            var ok = InvoiceJsonBuilder.Build(s, c);
            Assert.True(ok.Ok); Assert.Equal(44m, (decimal)ok.Json["tax_totals"]["other_charges"]);
        }

        [Fact]
        public void Freight_with_gst_needs_a_configured_sac_and_becomes_its_own_line()
        {
            var s = Src(); s.Expenses.Add(new GstExpense { Name = "Freight", AmountLc = 1000 });
            s.Taxes.Add(new GstTaxRow { LineNum = 0, RelateType = 3, TaxType = "-120", Rate = 18, AmountLc = 180, BaseLc = 1000 });
            Assert.False(InvoiceJsonBuilder.Build(s, Cfg()).Ok);
            var c = Cfg(); c.FreightSac = "996511"; s.DocTotalLc = 60180;
            var r = InvoiceJsonBuilder.Build(s, c);
            Assert.True(r.Ok, string.Join(";", r.Errors));
            Assert.Equal(2, ((JArray)r.Json["items"]).Count); Assert.Equal("996511", (string)r.Json["items"][1]["hsn_code"]);
            Assert.Equal(51000m, (decimal)r.Json["tax_totals"]["taxable_value"]); Assert.Equal(9180m, (decimal)r.Json["tax_totals"]["igst"]); Assert.Equal(0m, (decimal)r.Json["tax_totals"]["other_charges"]);
        }

        [Fact]
        public void Export_invoice_uses_urp_state_96_export_block_and_local_currency_amounts()
        {
            var s = Src(); s.Buyer.CountryIso2 = "DE"; s.Buyer.Gstin = ""; s.Buyer.StateCode = ""; s.DocCurrency = "EUR"; s.ForeignCurrency = true; s.ExchangeRate = 104.85m; s.DocTotalFc = 476.87m;
            s.Taxes.Clear(); s.DocTotalLc = 50000;
            var r = InvoiceJsonBuilder.Build(s, Cfg());
            Assert.True(r.Ok, string.Join(";", r.Errors));
            Assert.Equal("EXPWOP", (string)r.Json["supply_type"]); Assert.Equal("URP", (string)r.Json["buyer"]["gstin"]); Assert.Equal("96", (string)r.Json["buyer"]["state_code"]);
            Assert.Equal("EUR", (string)r.Json["currency"]); Assert.Equal("DE", (string)r.Json["export"]["country_code"]); Assert.Equal(476.87m, (decimal)r.Json["tax_totals"]["invoice_total_fc"]);
            var c = Cfg(); c.ExportWithPayment = true;
            Assert.Equal("EXPWP", (string)InvoiceJsonBuilder.Build(s, c).Json["supply_type"]);
        }

        [Fact]
        public void Missing_seller_gstin_or_cancelled_invoice_are_reported()
        {
            var c = Cfg(); c.SupplierGstin = "";
            Assert.Contains("seller GSTIN", string.Join(" ", InvoiceJsonBuilder.Build(Src(), c).Errors));
            var s = Src(); s.Cancelled = true;
            Assert.False(InvoiceJsonBuilder.Build(s, Cfg()).Ok);
        }

        [Fact]
        public void Units_hsn_and_state_are_never_replaced_silently()
        {
            var s = Src(); s.Lines[0].Uom = "Drum"; s.Lines[0].Hsn = ""; s.Lines[0].Sac = "";
            var r = InvoiceJsonBuilder.Build(s, Cfg());
            Assert.Equal("DRUM", (string)r.Json["items"][0]["unit"]);              // passed as is; the server rejects it
            Assert.Equal("", (string)r.Json["items"][0]["hsn_code"]);              // empty stays empty; the server rejects it
            var c = Cfg(); c.UqcMap["Drum"] = "DRM";
            Assert.Equal("DRM", (string)InvoiceJsonBuilder.Build(s, c).Json["items"][0]["unit"]);
            s.Buyer.Gstin = ""; s.Buyer.StateCode = "ZZ"; s.ShipStateCode = "ZZ";
            Assert.Equal("", (string)InvoiceJsonBuilder.Build(s, Cfg()).Json["buyer"]["state_code"]);
        }

        [Fact]
        public void Header_discount_is_applied_only_when_the_sap_tax_base_is_missing()
        {
            var s = Src(); s.HeaderDiscountPercent = 10; s.Taxes.Clear(); s.Taxes.Add(new GstTaxRow { LineNum = 0, RelateType = 1, TaxType = "-120", Rate = 18, AmountLc = 8100 });   // no BaseSum
            var r = InvoiceJsonBuilder.Build(s, Cfg());
            Assert.Equal(45000m, (decimal)r.Json["items"][0]["taxable_value"]);
        }

        [Fact]
        public void Jwt_payload_of_the_signed_invoice_can_be_displayed()
        {
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"data\":\"{\\\"DocDtls\\\":{\\\"No\\\":\\\"INV1\\\"}}\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var d = Jwt.DecodePayload("x." + payload + ".y");
            Assert.Equal("INV1", (string)d["DocDtls"]["No"]);
            Assert.Null(Jwt.DecodePayload("not-a-jwt"));
        }
    }

    /// <summary>Static proof that the 2.0 add-on has no ERPNext Sales Invoice / master-data dependency in its GST workflow.</summary>
    public class NoSalesInvoiceDependencyTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "EInvGstAddon.sln"))) d = d.Parent;
            return d == null ? null : d.FullName;
        }

        [Fact]
        public void The_ui_project_and_the_gst_path_contain_no_sales_invoice_or_master_data_code()
        {
            string root = Root(); Assert.NotNull(root);
            var files = Directory.GetFiles(Path.Combine(root, "src", "EInvGstAddon.UI"), "*.cs").Concat(new[]
            {
                Path.Combine(root, "src", "EInvGstAddon.ERPNext", "GstGateway.cs"), Path.Combine(root, "src", "EInvGstAddon.Core", "InvoiceJsonBuilder.cs"), Path.Combine(root, "src", "EInvGstAddon.Core", "ContextMenuModel.cs")
            });
            string[] forbidden = { "Sales Invoice", "/api/resource", "SyncEngine", "SyncQueue", "InvoiceSync", "CustomerSync", "ItemSync", "WarehouseSync", "GstService", "ErpSchema", "custom_sap_", "Payment Entry", "Stock Entry" };
            foreach (var f in files.Where(File.Exists))
            {
                string text = string.Join(Environment.NewLine, File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")));          // comments may explain what is NOT done
                foreach (var word in forbidden) Assert.False(text.Contains(word), Path.GetFileName(f) + " mentions " + word);
            }
        }

        [Fact]
        public void User_facing_screens_never_show_the_ERPNext_or_Frappe_brand()
        {
            string root = Root(); Assert.NotNull(root);
            var files = Directory.GetFiles(Path.Combine(root, "src", "EInvGstAddon.UI"), "*.cs").Concat(new[] { Path.Combine(root, "src", "EInvGstAddon.Core", "ContextMenuModel.cs"), Path.Combine(root, "src", "EInvGstAddon.ERPNext", "GstGateway.cs"), Path.Combine(root, "src", "EInvGstAddon", "Program.cs") });
            foreach (var f in files.Where(File.Exists))
                foreach (var line in File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("using ")))
                    foreach (var lit in System.Text.RegularExpressions.Regex.Matches(line, @"""(?:[^""\\]|\\.)*"""))
                    {
                        string t = lit.ToString();
                        if (t.Contains("frappe.") || t.Contains("/api/") || t.Contains("X-Frappe") || t.Contains("\"erpnext\"")) continue;          // backend method names / protocol keys
                        Assert.False(t.IndexOf("ERPNext", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Frappe", StringComparison.OrdinalIgnoreCase) >= 0, Path.GetFileName(f) + ": " + t);
                    }
        }

        [Fact]
        public void The_frappe_app_never_names_an_erpnext_accounting_or_master_doctype()
        {
            string root = Root(); Assert.NotNull(root);
            foreach (var f in Directory.GetFiles(Path.Combine(root, "frappe_app"), "*.py", SearchOption.AllDirectories).Where(x => !x.Contains("tests")))
            {
                string code = string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("#")));
                foreach (var dt in new[] { "\"Sales Invoice\"", "\"Customer\"", "\"Item\"", "\"Stock Ledger Entry\"", "\"GL Entry\"", "\"Payment Entry\"", "\"Stock Entry\"", "\"Warehouse\"" })
                    Assert.False(code.Contains(dt), Path.GetFileName(f) + " names " + dt);
            }
        }
    }
}
