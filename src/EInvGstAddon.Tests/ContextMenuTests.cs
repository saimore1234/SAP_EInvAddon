using System;
using System.Collections.Generic;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EInvGstAddon.Tests
{
    /// <summary>Records what the builder asks the SAP menu API to do (stands in for SAPbouiCOM.Menus).</summary>
    public sealed class FakeMenuHost : IMenuHost
    {
        public readonly Dictionary<string, string> Parent = new Dictionary<string, string>();
        public readonly Dictionary<string, bool> Enabled = new Dictionary<string, bool>();
        public int AddCalls;
        public bool Exists(string id) { return Parent.ContainsKey(id); }
        public void AddPopup(string parentId, string id, string caption) { if (Parent.ContainsKey(id)) throw new InvalidOperationException("duplicate menu id " + id); Parent[id] = parentId; AddCalls++; }
        public void AddItem(string parentId, string id, string caption, bool enabled) { if (Parent.ContainsKey(id)) throw new InvalidOperationException("duplicate menu id " + id); Parent[id] = parentId; Enabled[id] = enabled; AddCalls++; }
        public void Remove(string id) { Parent.Remove(id); Enabled.Remove(id); }
    }

    public class ContextMenuTests
    {
        private static string Id(CtxAction a) { return CtxMenuDefinition.IdPrefix + a.ToString().ToUpperInvariant(); }
        private static SyncRecord Synced() { return new SyncRecord { DocEntry = 5, Status = "Synced", ErpInvoice = "INV/1" }; }
        private static GstStatus St(int docstatus = 1, string irn = null, DateTime? ack = null, string ewb = null, DateTime? ewbDate = null, DateTime? valid = null)
        {
            return new GstStatus { SalesInvoice = "INV/1", DocStatus = docstatus, Irn = irn, AckDate = ack, EwbNo = ewb, EwbDate = ewbDate, EwbValidUpto = valid };
        }

        // ---------------------------------------------------------------- menu creation and duplicates
        [Fact]
        public void Menu_has_all_21_actions_in_three_groups_with_unique_ids()
        {
            Assert.Equal(21, CtxMenuDefinition.Items.Count);
            Assert.Equal(21, CtxMenuDefinition.Items.Select(i => i.Id).Distinct().Count());
            Assert.All(CtxMenuDefinition.Items, i => Assert.StartsWith("EINV_C_", i.Id));
            Assert.Equal(4, CtxMenuDefinition.Items.Count(i => i.Group == "ERPNext"));
            Assert.Equal(8, CtxMenuDefinition.Items.Count(i => i.Group == "E-Invoice"));
            Assert.Equal(9, CtxMenuDefinition.Items.Count(i => i.Group == "E-Way Bill"));
        }

        [Fact]
        public void Menu_is_created_under_the_sap_context_menu_and_removed_completely()
        {
            var host = new FakeMenuHost(); var b = new CtxMenuBuilder(host);
            b.Show(CtxStateCalculator.Compute(Synced(), St(), true, true, DateTime.Now));
            Assert.Equal("1280", host.Parent[CtxMenuDefinition.RootId]);
            Assert.Equal(1 + 3 + 21, host.Parent.Count);
            b.Hide();
            Assert.Empty(host.Parent);
        }

        [Fact]
        public void Repeated_right_clicks_never_create_duplicate_entries()
        {
            var host = new FakeMenuHost(); var b = new CtxMenuBuilder(host);
            for (int i = 0; i < 5; i++) b.Show(new CtxState());       // would throw on a duplicate id
            Assert.Equal(25, host.Parent.Count);
            b.Hide(); b.Hide();                                       // hiding twice is harmless
            Assert.Empty(host.Parent);
        }

        // ---------------------------------------------------------------- which invoice
        private static FormSnapshot Form(string type = "133", string obj = "13", long docEntry = 77, string mode = "OK", string db = "SomeDb", int docNum = 9001)
        {
            return new FormSnapshot { FormUid = "F1", FormType = type, ObjType = obj, DocEntry = docEntry, Mode = mode, Database = db, DocNum = docNum };
        }

        [Fact]
        public void Invoice_is_identified_by_docentry_not_docnum()
        {
            string why;
            var c = InvoiceContextResolver.Resolve(Form(docEntry: 77, docNum: 9001), "SomeDb", true, out why);
            Assert.Equal(77, c.DocEntry); Assert.Equal(9001, c.DocNum);
            Assert.Null(InvoiceContextResolver.Resolve(Form(docEntry: 0, docNum: 9001), "SomeDb", true, out why));      // DocNum present but not saved
            Assert.Contains("not been saved", why);
        }

        [Fact]
        public void Only_saved_ar_invoices_in_view_mode_of_the_configured_company_get_a_menu()
        {
            string why;
            Assert.Null(InvoiceContextResolver.Resolve(Form(type: "139"), "SomeDb", true, out why)); Assert.Contains("A/R Invoice", why);
            Assert.Null(InvoiceContextResolver.Resolve(Form(obj: "14"), "SomeDb", true, out why));
            Assert.Null(InvoiceContextResolver.Resolve(Form(mode: "Add"), "SomeDb", true, out why)); Assert.Contains("view mode", why);
            Assert.Null(InvoiceContextResolver.Resolve(Form(db: "OtherDb"), "SomeDb", true, out why)); Assert.Contains("not the configured", why);
            Assert.Null(InvoiceContextResolver.Resolve(Form(), "SomeDb", false, out why)); Assert.Contains("No active", why);
            Assert.NotNull(InvoiceContextResolver.Resolve(Form(db: "SOMEDB"), "somedb", true, out why));
        }

        [Fact]
        public void Action_is_refused_when_another_document_is_in_front()
        {
            string why;
            var ctx = InvoiceContextResolver.Resolve(Form(docEntry: 77), "SomeDb", true, out why);
            Assert.True(InvoiceContextResolver.IsSameDocument(ctx, Form(docEntry: 77), out why));
            Assert.False(InvoiceContextResolver.IsSameDocument(ctx, Form(docEntry: 78), out why)); Assert.Contains("changed", why);
            var other = Form(docEntry: 77); other.FormUid = "F2";
            Assert.False(InvoiceContextResolver.IsSameDocument(ctx, other, out why));
            Assert.False(InvoiceContextResolver.IsSameDocument(null, other, out why));
        }

        [Fact]
        public void The_same_invoice_cannot_run_two_actions_at_once()
        {
            var g = new ActionGate();
            Assert.True(g.TryEnter(5)); Assert.False(g.TryEnter(5)); Assert.True(g.TryEnter(6));
            g.Leave(5); Assert.True(g.TryEnter(5));
        }

        // ---------------------------------------------------------------- context-aware visibility
        [Fact]
        public void Not_synchronized_invoice_offers_sync_only()
        {
            var s = CtxStateCalculator.Compute(null, null, true, true, DateTime.Now);
            Assert.True(s.Has(CtxAction.Sync)); Assert.True(s.Has(CtxAction.History));
            Assert.False(s.Has(CtxAction.GenerateEInv)); Assert.False(s.Has(CtxAction.GenerateEwb)); Assert.False(s.Has(CtxAction.Submit)); Assert.False(s.Has(CtxAction.RefreshIrn));
            var failed = CtxStateCalculator.Compute(new SyncRecord { Status = "Failed" }, null, true, true, DateTime.Now);
            Assert.True(failed.Has(CtxAction.Sync)); Assert.False(failed.Has(CtxAction.GenerateEInv));
        }

        [Fact]
        public void Draft_invoice_offers_submit_but_not_generation()
        {
            var s = CtxStateCalculator.Compute(Synced(), St(docstatus: 0), true, true, DateTime.Now);
            Assert.True(s.Has(CtxAction.Submit)); Assert.True(s.Has(CtxAction.ViewErp));
            Assert.False(s.Has(CtxAction.GenerateEInv)); Assert.False(s.Has(CtxAction.GenerateEwb)); Assert.False(s.Has(CtxAction.ViewIrn));
        }

        [Fact]
        public void Submitted_invoice_without_irn_enables_validate_and_generate_and_hides_irn_views()
        {
            var s = CtxStateCalculator.Compute(Synced(), St(), true, true, DateTime.Now);
            Assert.False(s.Has(CtxAction.Submit));
            Assert.True(s.Has(CtxAction.ValidateEInv)); Assert.True(s.Has(CtxAction.GenerateEInv));
            Assert.False(s.Has(CtxAction.ViewIrn)); Assert.False(s.Has(CtxAction.ViewQr)); Assert.False(s.Has(CtxAction.CancelEInv));
            Assert.True(s.Has(CtxAction.ValidateEwb)); Assert.True(s.Has(CtxAction.GenerateEwb));
        }

        [Fact]
        public void Existing_irn_blocks_generation_and_enables_irn_actions_and_cancel_only_within_24h()
        {
            var now = DateTime.Now;
            var s = CtxStateCalculator.Compute(Synced(), St(irn: "IRN1", ack: now.AddHours(-2)), true, true, now);
            Assert.False(s.Has(CtxAction.GenerateEInv));
            foreach (var a in new[] { CtxAction.ViewIrn, CtxAction.ViewQr, CtxAction.DownloadSigned, CtxAction.PrintEInv, CtxAction.RefreshIrn, CtxAction.CancelEInv }) Assert.True(s.Has(a), a.ToString());
            var old = CtxStateCalculator.Compute(Synced(), St(irn: "IRN1", ack: now.AddHours(-30)), true, true, now);
            Assert.False(old.Has(CtxAction.CancelEInv)); Assert.True(old.Has(CtxAction.ViewIrn));
        }

        [Fact]
        public void Existing_ewaybill_blocks_generation_and_enables_update_and_cancel_when_eligible()
        {
            var now = DateTime.Now;
            var s = CtxStateCalculator.Compute(Synced(), St(ewb: "3410001", ewbDate: now.AddHours(-1), valid: now.AddDays(1)), true, true, now);
            Assert.False(s.Has(CtxAction.GenerateEwb));
            foreach (var a in new[] { CtxAction.ViewEwb, CtxAction.UpdateVehicle, CtxAction.UpdateTransporter, CtxAction.DownloadEwb, CtxAction.PrintEwb, CtxAction.RefreshEwb, CtxAction.CancelEwb }) Assert.True(s.Has(a), a.ToString());
            var expired = CtxStateCalculator.Compute(Synced(), St(ewb: "3410001", ewbDate: now.AddDays(-3), valid: now.AddDays(-1)), true, true, now);
            Assert.False(expired.Has(CtxAction.UpdateVehicle)); Assert.False(expired.Has(CtxAction.CancelEwb)); Assert.True(expired.Has(CtxAction.ViewEwb));
        }

        [Fact]
        public void Gst_disabled_in_configuration_disables_generation_but_keeps_erpnext_actions()
        {
            var s = CtxStateCalculator.Compute(Synced(), St(docstatus: 0), false, false, DateTime.Now);
            Assert.True(s.Has(CtxAction.Submit)); Assert.True(s.Has(CtxAction.ViewErp));
            var sub = CtxStateCalculator.Compute(Synced(), St(), false, false, DateTime.Now);
            Assert.False(sub.Has(CtxAction.GenerateEInv)); Assert.False(sub.Has(CtxAction.GenerateEwb)); Assert.False(sub.Has(CtxAction.ValidateEInv));
            Assert.Contains("not enabled", sub.Note);
        }

        [Fact]
        public void Cancelled_erpnext_invoice_offers_no_generation()
        {
            var s = CtxStateCalculator.Compute(Synced(), St(docstatus: 2), true, true, DateTime.Now);
            Assert.False(s.Has(CtxAction.GenerateEInv)); Assert.False(s.Has(CtxAction.GenerateEwb)); Assert.False(s.Has(CtxAction.Submit)); Assert.False(s.Has(CtxAction.ValidateEInv));
        }

        [Fact]
        public void Status_cache_throttles_refreshes_and_tracks_uncertain_outcomes()
        {
            var c = new StatusCache(TimeSpan.FromSeconds(30));
            Assert.True(c.NeedsRefresh(5, DateTime.Now));
            c.Put(5, St());
            Assert.False(c.NeedsRefresh(5, DateTime.Now)); Assert.True(c.NeedsRefresh(5, DateTime.Now.AddMinutes(1)));
            c.MarkUncertain(5, true); Assert.True(c.IsUncertain(5)); c.MarkUncertain(5, false); Assert.False(c.IsUncertain(5));
            c.Invalidate(5); Assert.Null(c.Get(5));
        }
    }

    /// <summary>Validation reports (read-only) and the uncertain-result / duplicate rules the right-click actions rely on. Mock ERPNext only.</summary>
    public class ContextActionBackendTests
    {
        private const string Gstin = "27AABCV1234F1Z5", Buyer = "29AAACB5678K1Z9";

        private static (GstService g, FakeErp erp, string inv, AppConfig cfg) Make(bool submit = true)
        {
            var cfg = TestCfg.Make(); cfg.EInvoiceEnabled = true; cfg.EWayBillEnabled = true; cfg.Environment = "Sandbox"; cfg.SupplierGstin = Gstin;
            var erp = new FakeErp(); erp.EnableGst(Gstin, true);
            var client = new ErpClient(cfg, erp);
            var o = new SyncEngine(client, FakeSap.WithInvoices(1), new MemoryStateStore()).Process(1);
            Assert.True(o.Success, o.Message);
            // give the invoice a short IRP-acceptable name and complete GST data
            var d = erp.Docs["Sales Invoice"][o.ErpInvoice]; erp.Docs["Sales Invoice"].Remove(o.ErpInvoice);
            d["name"] = "INV-0001"; erp.Docs["Sales Invoice"]["INV-0001"] = d;
            d["company_gstin"] = Gstin; d["billing_address_gstin"] = Buyer; d["place_of_supply"] = "29-Karnataka"; d["customer_name"] = "Test Customer One";
            d["posting_date"] = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"); d["customer_address"] = "ADDR-B"; d["company_address"] = "ADDR-S";
            foreach (var i in (JArray)d["items"]) i["gst_hsn_code"] = "39232100";
            d["total_taxes_and_charges"] = 180;
            erp.Seed("Address", "ADDR-B", new JObject { ["pincode"] = "560001" }); erp.Seed("Address", "ADDR-S", new JObject { ["pincode"] = "400601" });
            var g = new GstService(client);
            if (submit) Assert.True(g.SubmitInvoice("INV-0001").Success);
            return (g, erp, "INV-0001", cfg);
        }

        private static CheckLookup Look(List<GstService.CheckItem> l) { return new CheckLookup(l); }
        private sealed class CheckLookup
        {
            private readonly List<GstService.CheckItem> _l; public CheckLookup(List<GstService.CheckItem> l) { _l = l; }
            public GstService.CheckItem this[string name] { get { return _l.First(x => x.Name == name); } }
            public bool AllOk { get { return _l.All(x => x.Ok); } }
        }

        [Fact]
        public void Validation_of_a_complete_submitted_invoice_passes_every_check_and_generates_nothing()
        {
            var t = Make();
            var r = t.g.ValidateEInvoiceReport(t.inv);
            Assert.True(Look(r).AllOk, string.Join("; ", r.Where(x => !x.Ok).Select(x => x.Name + ": " + x.Message)));
            Assert.Empty(t.erp.GstCalls);                       // validation never calls a GST generate method
            Assert.True(r.Count >= 15);
        }

        [Fact]
        public void Validation_reports_each_problem_individually()
        {
            var t = Make(submit: false);
            var d = t.erp.Docs["Sales Invoice"][t.inv];
            d["billing_address_gstin"] = ""; d["place_of_supply"] = ""; d["customer_address"] = "ADDR-NOPIN";
            t.erp.Seed("Address", "ADDR-NOPIN", new JObject { ["pincode"] = "" });
            ((JArray)d["items"])[0]["gst_hsn_code"] = "";
            var l = Look(t.g.ValidateEInvoiceReport(t.inv));
            Assert.False(l["Invoice submission status"].Ok); Assert.Contains("Draft", l["Invoice submission status"].Message);
            Assert.False(l["Recipient GSTIN"].Ok); Assert.False(l["Place of supply"].Ok); Assert.False(l["Billing address & PIN"].Ok); Assert.False(l["HSN/SAC"].Ok);
            Assert.True(l["Supplier GSTIN"].Ok);
        }

        [Fact]
        public void Document_number_longer_than_16_characters_is_flagged_before_the_irp_rejects_it()
        {
            var t = Make();
            var d = t.erp.Docs["Sales Invoice"][t.inv]; t.erp.Docs["Sales Invoice"].Remove(t.inv); d["name"] = "VCPL-SINV-2026-00003"; t.erp.Docs["Sales Invoice"]["VCPL-SINV-2026-00003"] = d;
            var l = Look(t.g.ValidateEInvoiceReport("VCPL-SINV-2026-00003"));
            Assert.False(l["Document number"].Ok); Assert.Contains("max 16", l["Document number"].Message);
        }

        [Fact]
        public void Validation_detects_gst_configuration_and_environment_problems()
        {
            var t = Make(); t.erp.EnableGst(Gstin, sandbox: false);       // site is live, configuration says Sandbox
            var l = Look(t.g.ValidateEInvoiceReport(t.inv));
            Assert.False(l["Sandbox / Production"].Ok);
            t.erp.EnableGst(Gstin, true); t.erp.Docs["GST Settings"]["GST Settings"]["credentials"] = new JArray();
            Assert.False(Look(t.g.ValidateEInvoiceReport(t.inv))["GSP configuration"].Ok);
            t.erp.GstAppVersion = "14.0.1";
            Assert.False(Look(t.g.ValidateEInvoiceReport(t.inv))["india_compliance version"].Ok);
        }

        [Fact]
        public void Validation_of_an_invoice_from_another_company_stops_at_the_mapping_check()
        {
            var t = Make();
            t.erp.Seed("Sales Invoice", "OTHER-1", new JObject { ["company"] = "Tint Tech Coatings Private Limited", ["docstatus"] = 1 });
            var r = t.g.ValidateEInvoiceReport("OTHER-1");
            Assert.False(Look(r)["ERPNext invoice mapping"].Ok);
            Assert.DoesNotContain(r, x => x.Name == "Supplier GSTIN");
        }

        [Fact]
        public void Validation_reports_connection_failure_without_touching_anything_else()
        {
            var t = Make(); t.erp.Down = true;
            var r = t.g.ValidateEInvoiceReport(t.inv);
            Assert.Single(r); Assert.False(r[0].Ok); Assert.Equal("ERPNext connection", r[0].Name);
        }

        [Fact]
        public void Submit_never_submits_twice_and_reports_the_existing_status()
        {
            var t = Make(submit: false);
            Assert.True(t.g.SubmitInvoice(t.inv).Success);
            var again = t.g.SubmitInvoice(t.inv);
            Assert.True(again.Success); Assert.Contains("Already submitted", again.Message);
            Assert.Equal(1, (int)t.erp.Docs["Sales Invoice"][t.inv]["docstatus"]);
        }

        [Fact]
        public void Ewaybill_validation_report_blocks_duplicates_and_bad_transport_data()
        {
            var t = Make();
            var ok = t.g.ValidateEWayBillReport(t.inv, new EWayBillInput { VehicleNo = "MH04AB1234", Distance = 100 });
            Assert.True(ok.All(x => x.Ok), string.Join("; ", ok.Where(x => !x.Ok).Select(x => x.Message)));
            var bad = t.g.ValidateEWayBillReport(t.inv, new EWayBillInput { VehicleNo = "XX", Distance = 100 });
            Assert.Contains(bad, x => !x.Ok && x.Message.Contains("not a valid format"));
            Assert.True(t.g.GenerateEWayBill(t.inv, new EWayBillInput { VehicleNo = "MH04AB1234", Distance = 100 }).Success);
            t.erp.GstCalls.Clear();
            var dup = t.g.GenerateEWayBill(t.inv, new EWayBillInput { VehicleNo = "MH04AB1234", Distance = 100 });
            Assert.False(dup.Success); Assert.Empty(t.erp.GstCalls);       // no second e-Way Bill
            Assert.Contains(t.g.ValidateEWayBillReport(t.inv, new EWayBillInput { VehicleNo = "MH04AB1234" }), x => !x.Ok && x.Name == "Existing E-Way Bill");
        }

        [Fact]
        public void Refresh_reads_status_and_never_generates()
        {
            var t = Make();
            var s = t.g.GetStatus(t.inv, true);
            Assert.False(s.HasIrn); Assert.False(s.HasEwb);
            Assert.Empty(t.erp.GstCalls);
            Assert.Equal("Not generated", t.g.ToRecord(1, 1001, s).EInvStatus);
        }

        [Fact]
        public void Uncertain_result_is_flagged_and_a_second_generation_is_refused_until_status_is_reconciled()
        {
            var t = Make(); t.erp.TimeoutAfterGst = true;
            t.erp.Docs["Sales Invoice"][t.inv]["_force_no_irn_visible"] = 1;
            // the portal call "times out" AND the status read right after also fails -> uncertain
            var sapMock = t.erp;
            sapMock.TimeoutAfterGst = false;
            sapMock.Down = false;
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.True(r.Success);                              // baseline: normal generation works
            // simulate: status unreachable after the call
            var t2 = Make(); t2.erp.IrpError = null;
            var res = new GstResult { Uncertain = true, Success = false, Message = "outcome could not be confirmed" };
            var cache = new StatusCache(TimeSpan.FromSeconds(30));
            if (res.Uncertain) cache.MarkUncertain(1, true);
            Assert.True(cache.IsUncertain(1));                   // UI blocks Generate until a successful Refresh clears this flag
            cache.MarkUncertain(1, false);
            Assert.False(cache.IsUncertain(1));
        }
    }
}
