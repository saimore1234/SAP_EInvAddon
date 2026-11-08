using System;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace EInvGstAddon.Tests
{
    /// <summary>
    /// Live integration tests: read-only against the local SAP "Vipra" SQL database, writes ONLY to ERPNext company VCPL.
    /// Disabled unless EINV_LIVE=1. Credentials come from EINV_KEY / EINV_SECRET (never from files).
    /// EINV_DOCENTRIES (comma list) selects the SAP invoices to push.
    /// </summary>
    public class LiveTests
    {
        private readonly ITestOutputHelper _out;
        public LiveTests(ITestOutputHelper o) { _out = o; }

        private static bool Live { get { return Environment.GetEnvironmentVariable("EINV_LIVE") == "1"; } }

        private static AppConfig LiveCfg()
        {
            var c = new AppConfig { ApiKey = Environment.GetEnvironmentVariable("EINV_KEY") };
            c.ApiSecret = Environment.GetEnvironmentVariable("EINV_SECRET");
            return c;
        }

        private static SapReader Reader() { return new SapReader(new SqlClientRunner("Server=localhost;Database=Vipra;Integrated Security=true;TrustServerCertificate=true"), "Vipra"); }

        [Fact]
        public void Live_connection_and_company()
        {
            if (!Live) return;
            using (var erp = new ErpClient(LiveCfg()))
            {
                Assert.False(string.IsNullOrEmpty((string)erp.Method("frappe.auth.get_logged_user")["message"]));
                var co = erp.GetDoc("Company", "Vipra Closures Private Limited");
                Assert.Equal("VCPL", (string)co["abbr"]);
                _out.WriteLine("Company OK: " + co["name"] + " gstin='" + co["gstin"] + "'");
            }
        }

        [Fact]
        public void Live_schema_is_created_idempotently()
        {
            if (!Live) return;
            using (var erp = new ErpClient(LiveCfg()))
            {
                var s = new ErpSchema(erp);
                var created = s.Ensure();
                _out.WriteLine("created: " + string.Join(", ", created));
                Assert.Empty(s.Missing());
                Assert.Empty(s.Ensure());
            }
        }

        [Fact]
        public void Live_sync_sap_invoices_to_vcpl_draft()
        {
            if (!Live) return;
            var ids = (Environment.GetEnvironmentVariable("EINV_DOCENTRIES") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            if (ids.Count == 0) return;
            var cfg = LiveCfg();
            var store = new MemoryStateStore();
            var sap = Reader();
            using (var erp = new ErpClient(cfg))
            {
                var engine = new SyncEngine(erp, sap, store);
                foreach (var id in ids)
                {
                    var o = engine.Process(id);
                    _out.WriteLine("DocEntry " + id + " => " + o.Status + " " + o.ErpInvoice + " " + o.ErpDocStatus + " total=" + o.ErpGrandTotal + " custCreated=" + o.CustomerCreated + " itemsCreated=" + o.ItemsCreated);
                    _out.WriteLine("   message: " + o.Message);
                    foreach (var w in o.Warnings) _out.WriteLine("   warn: " + w);
                    Assert.True(o.Success, o.Message);
                    var inv = sap.GetInvoice(id);
                    var si = erp.GetDoc("Sales Invoice", o.ErpInvoice);
                    Assert.Equal("Vipra Closures Private Limited", (string)si["company"]);
                    Assert.Equal(0, (int)si["docstatus"]);
                    _out.WriteLine("   SAP total " + inv.DocTotal + " | ERP grand " + si["grand_total"] + " rounded " + si["rounded_total"] + " net " + si["net_total"] + " | SAP vat " + inv.VatTotal + " ERP taxes " + si["total_taxes_and_charges"]);
                    // second pass must be idempotent
                    store.SaveRecord(new SyncRecord { DocEntry = id, Database = "Vipra", Status = SyncStatus.Failed });
                    var again = engine.Process(id);
                    Assert.Equal(o.ErpInvoice, again.ErpInvoice);
                }
                var all = erp.GetList("Sales Invoice", new[] { "name" }, new object[] { new object[] { "custom_sap_database", "=", "Vipra" } }, 100);
                _out.WriteLine("Vipra sales invoices in ERPNext: " + all.Count);
                Assert.True(all.Count >= ids.Distinct().Count());
            }
        }
    }
}

namespace EInvGstAddon.Tests
{
    /// <summary>Read-only diagnostic using the add-on's own saved configuration (DPAPI, same Windows user). Prints nothing secret.</summary>
    public class LiveDiagnostics
    {
        private readonly ITestOutputHelper _out;
        public LiveDiagnostics(ITestOutputHelper o) { _out = o; }

        [Fact]
        public void Hsn_lookup()
        {
            if (Environment.GetEnvironmentVariable("EINV_LIVE") != "1") return;
            var cfg = ConfigStore.Load();
            using (var erp = new ErpClient(cfg))
            {
                foreach (var c in new[] { "76061220", "760612", "7606", "76061110", "39232100" })
                    _out.WriteLine(c + " exists=" + (erp.GetDoc("GST HSN Code", c) != null));
                var like = erp.GetList("GST HSN Code", new[] { "name", "description" }, new object[] { new object[] { "name", "like", "7606%" } }, 20);
                _out.WriteLine("7606% codes: " + string.Join(", ", like.Select(x => (string)x["name"])));
                var item = erp.GetDoc("Item", "SAMPLE SHEETS");
                _out.WriteLine("item exists=" + (item != null) + (item != null ? " hsn='" + item["gst_hsn_code"] + "'" : ""));
                _out.WriteLine("hsn validation settings: " + string.Join(", ", new[] { "validate_hsn_code", "min_hsn_digits" }.Select(k => k + "=" + erp.GetDoc("GST Settings", "GST Settings")[k])));
            }
        }
    }
}

namespace EInvGstAddon.Tests
{
    public class LiveVerify
    {
        private readonly ITestOutputHelper _out;
        public LiveVerify(ITestOutputHelper o) { _out = o; }

        [Fact]
        public void Show_vipra_invoices_after()
        {
            if (Environment.GetEnvironmentVariable("EINV_LIVE") != "1") return;
            int from = int.Parse(Environment.GetEnvironmentVariable("EINV_FROM") ?? "2624");
            using (var erp = new ErpClient(ConfigStore.Load()))
            {
                var l = erp.GetList("Sales Invoice", new[] { "name", "docstatus", "company", "customer_name", "currency", "net_total", "total_taxes_and_charges", "grand_total", "rounded_total", "custom_sap_docentry", "irn", "naming_series" },
                    new object[] { new object[] { "custom_sap_database", "=", "Vipra" }, new object[] { "custom_sap_docentry", ">=", from.ToString() } }, 50);
                foreach (var x in l) _out.WriteLine(x.ToString(Newtonsoft.Json.Formatting.None));
                _out.WriteLine("count=" + l.Count);
            }
        }
    }
}
