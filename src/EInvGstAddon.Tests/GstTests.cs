using System;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EInvGstAddon.Tests
{
    /// <summary>All GST tests run against the mock; no real IRP / GSP / production credential is contacted.</summary>
    public class GstTests
    {
        private const string Gstin = "27AABCV1234F1Z5", Buyer = "29AAACB5678K1Z9";

        private static (GstService g, FakeErp erp, string inv, AppConfig cfg) Make(bool submitted = true, bool enable = true)
        {
            var cfg = TestCfg.Make(); cfg.EInvoiceEnabled = enable; cfg.EWayBillEnabled = enable; cfg.Environment = "Sandbox"; cfg.SupplierGstin = Gstin;
            var erp = new FakeErp(); erp.EnableGst(Gstin, true);
            var client = new ErpClient(cfg, erp);
            var sap = FakeSap.WithInvoices(1); var store = new MemoryStateStore();
            var o = new SyncEngine(client, sap, store).Process(1);
            Assert.True(o.Success, o.Message);
            var d = erp.Docs["Sales Invoice"][o.ErpInvoice];
            d["company_gstin"] = Gstin; d["billing_address_gstin"] = Buyer; d["place_of_supply"] = "29-Karnataka"; d["customer_name"] = "Test Customer One";
            var g = new GstService(client);
            if (submitted) Assert.True(g.SubmitInvoice(o.ErpInvoice).Success);
            return (g, erp, o.ErpInvoice, cfg);
        }

        [Fact]
        public void Draft_invoice_cannot_get_an_irn_and_nothing_is_called()
        {
            var t = Make(submitted: false);
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("Draft", r.Message);
            Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Disabled_gst_in_configuration_blocks_generation_without_affecting_sync()
        {
            var t = Make(enable: false);
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("not enabled", r.Message); Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Einvoice_is_generated_only_through_the_installed_gst_method_and_read_back()
        {
            var t = Make();
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.True(r.Success, r.Message);
            Assert.Equal(new[] { "generate_e_invoice" }, t.erp.GstCalls.ToArray());
            Assert.True(r.Status.HasIrn); Assert.Equal("112233445566", r.Status.AckNo); Assert.Equal("SIGNED.QR.TOKEN", r.Status.SignedQr); Assert.Equal("SIGNED.INVOICE", r.Status.SignedInvoice);
            Assert.NotNull(r.Status.AckDate);
        }

        [Fact]
        public void Existing_irn_is_never_regenerated()
        {
            var t = Make();
            Assert.True(t.g.GenerateEInvoice(t.inv).Success);
            t.erp.GstCalls.Clear();
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("already exists", r.Message); Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Irp_rejection_is_reported_and_no_irn_is_invented()
        {
            var t = Make(); t.erp.IrpError = "Invalid GSTIN of the recipient";
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("Invalid GSTIN", r.Message); Assert.False(r.Status.HasIrn);
        }

        [Fact]
        public void Lost_response_is_not_retried_and_a_server_side_success_is_still_detected()
        {
            var t = Make(); t.erp.TimeoutAfterGst = true;
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.True(r.Success, r.Message);                      // status re-read shows the real IRN
            Assert.Single(t.erp.GstCalls);                          // exactly one call, no blind repeat
        }

        [Fact]
        public void Production_gst_settings_with_sandbox_configuration_is_blocked()
        {
            var t = Make(); t.erp.EnableGst(Gstin, sandbox: false);
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("PRODUCTION", r.Message); Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Missing_company_credentials_or_gstin_blocks_gst_actions()
        {
            var t = Make(); t.erp.Docs["GST Settings"]["GST Settings"]["credentials"] = new JArray();
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("No GSP credentials", r.Message);
            t.erp.EnableGst("");
            Assert.Contains("no GSTIN", t.g.ValidateGstConfiguration().ToString());
        }

        [Fact]
        public void B2c_invoice_is_not_applicable_for_einvoice()
        {
            var t = Make(); t.erp.Docs["Sales Invoice"][t.inv]["billing_address_gstin"] = "";
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("B2C", r.Message);
        }

        [Fact]
        public void Einvoice_cancellation_requires_reason_and_24_hours_and_is_confirmed_by_erpnext()
        {
            var t = Make(); t.g.GenerateEInvoice(t.inv);
            Assert.False(t.g.CancelEInvoice(t.inv, "", "x").Success);
            t.erp.Docs["e-Invoice Log"].Values.First()["acknowledged_on"] = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss");
            Assert.Contains("24 hours", t.g.CancelEInvoice(t.inv, "Data Entry Mistake", "x").Message);
            t.erp.Docs["e-Invoice Log"].Values.First()["acknowledged_on"] = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
            var r = t.g.CancelEInvoice(t.inv, "Data Entry Mistake", "wrong customer");
            Assert.True(r.Success, r.Message); Assert.False(r.Status.HasIrn); Assert.True(r.Status.IrnCancelled);
        }

        [Fact]
        public void Ewaybill_validation_rejects_bad_transport_data_before_any_call()
        {
            var t = Make();
            var r = t.g.GenerateEWayBill(t.inv, new EWayBillInput { ModeOfTransport = "Road", VehicleNo = "BADVEHICLE", Distance = 100 });
            Assert.False(r.Success); Assert.Contains("not a valid format", r.Message);
            r = t.g.GenerateEWayBill(t.inv, new EWayBillInput { ModeOfTransport = "Road", Distance = 100 });
            Assert.Contains("vehicle number or a transporter", r.Message);
            r = t.g.GenerateEWayBill(t.inv, new EWayBillInput { ModeOfTransport = "Road", VehicleNo = "MH04AB1234", Distance = 99999 });
            Assert.Contains("Distance", r.Message);
            Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Ewaybill_generate_update_vehicle_update_transporter_and_cancel()
        {
            var t = Make();
            var g = t.g.GenerateEWayBill(t.inv, new EWayBillInput { ModeOfTransport = "Road", VehicleNo = "MH04AB1234", TransporterId = "27AAACT1234A1Z5", Distance = 120 });
            Assert.True(g.Success, g.Message); Assert.True(g.Status.HasEwb); Assert.NotNull(g.Status.EwbValidUpto);
            var v = t.g.UpdateVehicle(t.inv, new EWayBillInput { VehicleNo = "MH12CD5678", ModeOfTransport = "Road" }, "Due to Break Down", "tyre", "Thane", "Maharashtra");
            Assert.True(v.Success, v.Message); Assert.Equal("MH12CD5678", v.Status.VehicleNo);
            var tr = t.g.UpdateTransporter(t.inv, "29AAACT9999A1Z1");
            Assert.True(tr.Success, tr.Message);
            t.erp.Docs["e-Waybill Log"].Values.First()["created_on"] = DateTime.Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss");
            var c = t.g.CancelEWayBill(t.inv, "Order Cancelled", "customer cancelled");
            Assert.True(c.Success, c.Message); Assert.False(c.Status.HasEwb);
            Assert.Equal(new[] { "generate_e_waybill", "update_vehicle_info", "update_transporter", "cancel_e_waybill" }, t.erp.GstCalls.ToArray());
        }

        [Fact]
        public void Incompatible_gst_app_version_disables_gst_actions()
        {
            var t = Make(); t.erp.GstAppVersion = "14.2.0";
            var r = t.g.GenerateEInvoice(t.inv);
            Assert.False(r.Success); Assert.Contains("Unsupported versions", r.Message); Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Invoice_of_another_company_is_refused()
        {
            var t = Make();
            t.erp.Seed("Sales Invoice", "OTHER-1", new JObject { ["company"] = "Tint Tech Coatings Private Limited", ["docstatus"] = 1 });
            Assert.Throws<ConfigurationException>(() => t.g.GetStatus("OTHER-1"));
            Assert.Throws<ConfigurationException>(() => t.g.GenerateEInvoice("OTHER-1"));
            Assert.Empty(t.erp.GstCalls);
        }

        [Fact]
        public void Gst_record_for_sap_carries_actual_values_only()
        {
            var t = Make(); t.g.GenerateEInvoice(t.inv);
            var rec = t.g.ToRecord(1, 1001, t.g.GetStatus(t.inv, true));
            Assert.Equal("Generated", rec.EInvStatus); Assert.Equal("112233445566", rec.AckNo); Assert.Equal("SIGNED.QR.TOKEN", rec.SignedQr); Assert.StartsWith("IRN", rec.Irn);
            Assert.Equal("Not generated", rec.EwbStatus);
        }
    }
}
