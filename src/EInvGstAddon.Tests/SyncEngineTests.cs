using System;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EInvGstAddon.Tests
{
    public class SyncEngineTests
    {
        private static AppConfig Cfg()
        {
            var c = new AppConfig { ErpUrl = "https://erp.example.test", ApiKey = "key123456", SapDatabase = "Vipra" };
            c.ApiSecret = "secret123456";
            return c;
        }

        private static (SyncEngine engine, FakeErp erp, FakeSap sap, MemoryStateStore store, AppConfig cfg) Make(params int[] docs)
        {
            var cfg = Cfg(); var erp = new FakeErp(); var sap = FakeSap.WithInvoices(docs); var store = new MemoryStateStore();
            var client = new ErpClient(cfg, erp);
            return (new SyncEngine(client, sap, store), erp, sap, store, cfg);
        }

        [Fact]
        public void Creates_draft_invoice_customer_items_and_uses_token_auth()
        {
            var t = Make(1);
            var o = t.engine.Process(1);
            Assert.True(o.Success, o.Message);
            Assert.Equal("token key123456:secret123456", t.erp.LastAuth);
            var si = t.erp.All("Sales Invoice").Single();
            Assert.Equal(0, (int)si["docstatus"]);
            Assert.Equal("Vipra Closures Private Limited", (string)si["company"]);
            Assert.Equal("Vipra-13-1", (string)si["custom_sap_sync_reference"]);
            Assert.Equal("VCPL-SINV-.YYYY.-.#####", (string)si["naming_series"]);
            Assert.Equal(1, t.erp.All("Customer").Count());
            Assert.Equal("Synced", t.store.GetRecord(1).Status);
            Assert.Equal(o.ErpInvoice, t.store.GetRecord(1).ErpInvoice);
            Assert.DoesNotContain(t.erp.Calls, c => c.Contains("e_invoice") || c.Contains("e_waybill"));
        }

        [Fact]
        public void Retrying_same_invoice_never_creates_a_second_sales_invoice()
        {
            var t = Make(1);
            var first = t.engine.Process(1);
            // simulate a lost SAP state (UDT row missing) -> engine must find the ERPNext invoice by SAP reference
            var rec = t.store.GetRecord(1); rec.Status = SyncStatus.Failed; rec.ErpInvoice = null; t.store.SaveRecord(rec);
            var second = t.engine.Process(1);
            Assert.True(second.Success);
            Assert.Equal(first.ErpInvoice, second.ErpInvoice);
            Assert.Single(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Second_invoice_for_same_customer_reuses_customer_and_items()
        {
            var t = Make(1, 2);
            t.engine.Process(1); t.engine.Process(2);
            Assert.Equal(1, t.erp.All("Customer").Count());
            Assert.Equal(1, t.erp.All("Item").Count());
            Assert.Equal(2, t.erp.All("Sales Invoice").Count());
        }

        [Fact]
        public void ERPNext_down_leaves_invoice_pending_and_recovery_completes_it()
        {
            var t = Make(1);
            t.erp.Down = true;
            var o = t.engine.Process(1);
            Assert.False(o.Success);
            Assert.Equal(SyncStatus.Pending, t.store.GetRecord(1).Status);
            Assert.Equal(1, t.store.GetRecord(1).RetryCount);
            Assert.Empty(t.erp.All("Sales Invoice"));
            t.erp.Down = false;
            Assert.True(t.engine.Process(1).Success);
            Assert.Single(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Lost_response_after_create_does_not_duplicate_on_retry()
        {
            var t = Make(1);
            t.erp.FailAfterInsertOfSalesInvoice = true;
            var o = t.engine.Process(1);
            Assert.False(o.Success);                       // timeout seen by the add-on
            Assert.Equal(SyncStatus.Pending, t.store.GetRecord(1).Status);
            var again = t.engine.Process(1);               // retry must discover the already-created document
            Assert.True(again.Success);
            Assert.Single(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Warehouse_of_other_company_is_never_used()
        {
            var t = Make(1);
            t.store.SaveWarehouseMap(new MapEntry { SapKey = "W1", ErpName = "Stores - TTCPL" });
            var o = t.engine.Process(1);
            Assert.False(o.Success);
            Assert.Equal(SyncStatus.Failed, o.Status);
            Assert.Contains("belongs to company", o.Message);
            Assert.Empty(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Unmapped_tax_type_stops_the_sync_with_a_clear_error()
        {
            var t = Make(1);
            t.sap.Invoices[1].Taxes.Add(new SapTaxAmount { TaxType = "-90", TaxCode = "CESS", Rate = 1, Amount = 10 });
            var o = t.engine.Process(1);
            Assert.False(o.Success);
            Assert.Contains("Tax configuration missing", o.Message);
            Assert.Empty(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Tax_account_of_other_company_is_refused()
        {
            var t = Make(1);
            t.cfg.TaxAccountMap["-120"] = "Output Tax IGST - TTCPL";
            t.sap.Invoices[1].Taxes.Add(new SapTaxAmount { TaxType = "-120", TaxCode = "IGST", Rate = 18, Amount = 5 });
            var o = t.engine.Process(1);
            Assert.False(o.Success);
            Assert.Empty(t.erp.All("Sales Invoice"));
        }

        [Fact]
        public void Client_refuses_to_write_documents_for_another_company()
        {
            var cfg = Cfg(); var erp = new FakeErp();
            var client = new ErpClient(cfg, erp);
            Assert.Throws<ConfigurationException>(() => client.Insert("Sales Invoice", new JObject { ["company"] = "Tint Tech Coatings Private Limited" }));
            Assert.Throws<ConfigurationException>(() => client.Update("Account", "Output Tax IGST - TTCPL", new JObject { ["x"] = 1 }));
            Assert.Empty(erp.Calls);
        }

        [Fact]
        public void Totals_match_sap_and_tax_rows_carry_exact_amounts()
        {
            var t = Make(1);
            var o = t.engine.Process(1);
            Assert.Equal(1180m, o.ErpGrandTotal);
            var si = t.erp.All("Sales Invoice").Single();
            Assert.Equal(new[] { 90m, 90m }, si["taxes"].Select(x => (decimal)x["tax_amount"]).ToArray());
            Assert.Empty(o.Warnings.Where(w => w.StartsWith("Total differs")));
        }

        [Fact]
        public void Cancelled_and_service_invoices_are_not_synchronised()
        {
            var t = Make(1, 2);
            t.sap.Invoices[1].Cancelled = true; t.sap.Invoices[2].DocType = "S";
            Assert.False(t.engine.Process(1).Success);
            Assert.False(t.engine.Process(2).Success);
            Assert.Empty(t.erp.All("Sales Invoice"));
        }
    }
}

namespace EInvGstAddon.Tests
{
    public class PrecisionTests
    {
        [Fact]
        public void Unit_price_beyond_erp_precision_is_compensated_so_totals_match_sap()
        {
            // real Vipra invoice 2617 line: 95000 x 0.158 = 15010 in SAP; ERPNext (2 decimals) would hold 0.16 -> 15200
            var inv = new SapInvoice();
            inv.Lines.Add(new SapInvoiceLine { Quantity = 95000, UnitPrice = 0.158m, LineTotal = 15010m });
            inv.Lines.Add(new SapInvoiceLine { Quantity = 215, UnitPrice = 488.65m, LineTotal = 105059.75m });
            Assert.Equal(-190m, InvoiceSync.PrecisionDifference(inv, 2));
            Assert.Equal(0m, InvoiceSync.PrecisionDifference(inv, 3));
        }

        [Fact]
        public void Precision_difference_row_is_added_to_the_draft()
        {
            var cfg = new AppConfig { ErpUrl = "https://erp.example.test", ApiKey = "key123456", SapDatabase = "Vipra" }; cfg.ApiSecret = "secret123456";
            var erp = new FakeErp(); var sap = FakeSap.WithInvoices(1); var store = new MemoryStateStore();
            sap.Invoices[1].Lines[0].UnitPrice = 0.158m; sap.Invoices[1].Lines[0].Quantity = 1000; sap.Invoices[1].Lines[0].LineTotal = 158m;
            var o = new SyncEngine(new ErpClient(cfg, erp), sap, store).Process(1);
            Assert.True(o.Success, o.Message);
            var rows = erp.All("Sales Invoice").Single()["taxes"];
            Assert.Contains(rows, r => (string)r["account_head"] == "Rounded Off - VCPL" && (decimal)r["tax_amount"] == -2m);
        }
    }
}
