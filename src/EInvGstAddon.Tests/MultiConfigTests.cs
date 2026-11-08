using System;
using System.IO;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EInvGstAddon.Tests
{
    [Collection("config-file")]
    public class ConfigStoreTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "einv-test-" + Guid.NewGuid().ToString("N") + ".json");
        public ConfigStoreTests() { ConfigStore.PathOverride = _path; }
        public void Dispose() { ConfigStore.PathOverride = null; try { File.Delete(_path); } catch { } }

        private static AppConfig P(string db, string company, bool active = true)
        {
            var c = new AppConfig { SapDatabase = db, ErpUrl = "https://erp.example.test", ApiKey = "key-" + db, ErpCompany = company, ErpAbbr = "X", Active = active, Name = db };
            c.ApiSecret = "super-secret-" + db;
            return c;
        }

        [Fact]
        public void Legacy_phase1_file_is_migrated_and_keeps_its_reference_format()
        {
            File.WriteAllText(_path, "{\"ErpUrl\":\"https://erp.example.test\",\"ApiKey\":\"k\",\"ErpCompany\":\"Legacy Co\",\"ErpAbbr\":\"LC\",\"SapDatabase\":\"Legacydb\",\"AutoSync\":true}");
            var f = ConfigStore.LoadFile();
            var p = Assert.Single(f.Profiles);
            Assert.Equal("default", p.Id); Assert.False(p.ReferenceIncludesConfigId); Assert.Equal("", p.ReferenceSuffix); Assert.Equal("", p.StoredConfigId);
            var inv = new SapInvoice { Database = "Legacydb", ObjType = "13", DocEntry = 5, RefSuffix = p.ReferenceSuffix };
            Assert.Equal("Legacydb-13-5", inv.SyncReference);
        }

        [Fact]
        public void New_profiles_carry_their_id_in_every_reference()
        {
            var p = P("ABC", "ABC Manufacturing Private Limited"); p.Id = "a1b2c3d4";
            var inv = new SapInvoice { Database = "ABC", ObjType = "13", DocEntry = 7, RefSuffix = p.ReferenceSuffix };
            Assert.Equal("ABC-13-7-a1b2c3d4", inv.SyncReference);
            Assert.Equal("a1b2c3d4", p.StoredConfigId);
        }

        [Fact]
        public void Only_one_active_configuration_per_sap_database_and_lookup_follows_the_open_database()
        {
            var f = new ConfigFile();
            f.Profiles.Add(P("Vipra", "Vipra Co")); f.Profiles.Add(P("ABC", "ABC Co")); f.Profiles.Add(P("XYZ", "XYZ Co", active: false));
            foreach (var p in f.Profiles) p.Id = AppConfig.NewId();
            ConfigStore.SaveFile(f);
            Assert.Equal("ABC Co", ConfigStore.FindFor("abc").ErpCompany);
            Assert.Null(ConfigStore.FindFor("XYZ"));                    // inactive
            Assert.Null(ConfigStore.FindFor("Unknown"));
            f.Profiles.Add(P("ABC", "ABC Other Co")); f.Profiles.Last().Id = AppConfig.NewId();
            Assert.Throws<InvalidOperationException>(() => ConfigStore.SaveFile(f));
        }

        [Fact]
        public void Secrets_are_never_written_in_clear_text()
        {
            var p = P("ABC", "ABC Co"); p.Id = "id1"; ConfigStore.Upsert(p);
            string text = File.ReadAllText(_path);
            Assert.DoesNotContain("super-secret-ABC", text);
            Assert.Contains("ApiSecretProtected", text);
            Assert.Equal("super-secret-ABC", ConfigStore.LoadFile().Profiles.Single().ApiSecret);
        }

        [Fact]
        public void Protected_company_cannot_be_a_target()
        {
            var p = P("ABC", "Other Co"); p.ProtectedCompanies.Add("Other Co");
            Assert.Throws<InvalidOperationException>(() => p.ValidateTarget());
        }

        [Fact]
        public void Gst_automation_requires_a_validated_gst_configuration()
        {
            var p = P("ABC", "ABC Co");
            Assert.False(p.AutoEInvoice); Assert.False(p.AutoEWayBill);        // defaults: off
            Assert.False(p.GstReady);
            p.SupplierGstin = "27AABCV1234F1Z5"; Assert.False(p.GstReady);     // GSTIN alone is not enough
            p.GspStatus = "Configured"; Assert.True(p.GstReady);
        }
    }

    /// <summary>A second, fictional customer: same code, different SAP database + ERPNext company. No value below exists in the product code.</summary>
    public class SecondCustomerTests
    {
        private static (SyncEngine e, FakeErp erp, FakeSap sap, AppConfig cfg) Make()
        {
            var cfg = new AppConfig
            {
                Id = "abc12345", Name = "ABC", SapDatabase = "ABC", ErpUrl = "https://erp.example.test", ApiKey = "key123456", ErpCompany = "ABC Manufacturing Private Limited", ErpAbbr = "ABC",
                FreightAccount = "Freight - ABC", RoundOffAccount = "Round Off - ABC"
            };
            cfg.ApiSecret = "secret123456";
            cfg.TaxAccountMap["-100"] = "Output CGST - ABC"; cfg.TaxAccountMap["-110"] = "Output SGST - ABC"; cfg.TaxAccountMap["-120"] = "Output IGST - ABC";
            var erp = new FakeErp();
            foreach (var n in new[] { "Output CGST - ABC", "Output SGST - ABC", "Output IGST - ABC", "Freight - ABC", "Round Off - ABC" }) erp.Seed("Account", n, new JObject { ["company"] = "ABC Manufacturing Private Limited" });
            erp.Seed("Company", "ABC Manufacturing Private Limited", new JObject { ["abbr"] = "ABC", ["default_currency"] = "INR", ["gstin"] = "" });
            erp.Seed("Warehouse", "All Warehouses - ABC", new JObject { ["company"] = "ABC Manufacturing Private Limited", ["is_group"] = 1 });
            var sap = FakeSap.WithInvoices(1);
            sap.Invoices[1].Database = "ABC"; sap.Invoices[1].Currency = "INR"; sap.Partners["C1"].Currency = "INR";
            return (new SyncEngine(new ErpClient(cfg, erp), sap, new MemoryStateStore()), erp, sap, cfg);
        }

        [Fact]
        public void Invoice_lands_in_the_second_customers_company_with_its_own_reference_and_warehouse_root()
        {
            var t = Make();
            var o = t.e.Process(1);
            Assert.True(o.Success, o.Message);
            var si = t.erp.All("Sales Invoice").Single();
            Assert.Equal("ABC Manufacturing Private Limited", (string)si["company"]);
            Assert.Equal("ABC-13-1-abc12345", (string)si["custom_sap_sync_reference"]);
            Assert.Equal("W1 - ABC", t.erp.All("Warehouse").First(w => (string)w["warehouse_name"] == "W1")["name"].ToString());
            Assert.Equal(new[] { "Output CGST - ABC", "Output SGST - ABC" }, si["taxes"].Select(x => (string)x["account_head"]).ToArray());
        }

        [Fact]
        public void Another_customers_account_or_warehouse_is_never_used()
        {
            var t = Make();
            t.cfg.TaxAccountMap["-100"] = "Output Tax CGST - VCPL";          // belongs to a different customer's company on the same site
            var o = t.e.Process(1);
            Assert.False(o.Success); Assert.Contains("belongs to", o.Message);
            Assert.Empty(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Two_configurations_on_the_same_site_do_not_collide_on_references()
        {
            var a = Make();
            var o = a.e.Process(1); Assert.True(o.Success, o.Message);
            // same SAP database + DocEntry synchronised through a second configuration targeting the same company: different reference -> its own invoice
            var cfg2 = a.cfg.Clone(); cfg2.Id = "zzz99999"; cfg2.ApiSecret = "secret123456";
            var store2 = new MemoryStateStore();
            var o2 = new SyncEngine(new ErpClient(cfg2, a.erp), a.sap, store2).Process(1);
            Assert.True(o2.Success, o2.Message);
            Assert.NotEqual(o.ErpInvoice, o2.ErpInvoice);
            Assert.Equal(2, a.erp.All("Sales Invoice").Count());
        }

        [Fact]
        public void Missing_freight_account_stops_only_the_invoice_that_needs_it()
        {
            var t = Make(); t.cfg.FreightAccount = "";
            t.sap.Invoices[1].Freight = 100;
            var o = t.e.Process(1);
            Assert.False(o.Success); Assert.Contains("No ERPNext account is configured", o.Message);
        }
    }
}
