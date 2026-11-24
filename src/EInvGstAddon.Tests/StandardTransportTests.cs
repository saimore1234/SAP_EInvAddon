using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EInvGstAddon.Core;
using Xunit;

namespace EInvGstAddon.Tests
{
    /// <summary>The E-Way Bill transport details are read from SAP's standard form (INV26) of the selected A/R Invoice; nothing is typed twice and nothing is defaulted.</summary>
    public class StandardTransportTests
    {
        private sealed class FakeSql : ISqlRunner
        {
            public readonly List<string> Sql = new List<string>(); public Dictionary<string, object> Row;
            public List<Dictionary<string, object>> Query(string sql) { Sql.Add(sql); return Row == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>> { Row }; }
        }

        // Real shape of an INV26 row (SAP B1 10.0): values as SQL Server returns them.
        private static Dictionary<string, object> Row(Action<Dictionary<string, object>> tweak = null)
        {
            var r = new Dictionary<string, object> { ["TransMode"] = 1, ["Distance"] = 1000m, ["TransDocNo"] = null, ["TransDate"] = null, ["VehicleTyp"] = "R", ["VehicleNo"] = "MH46H7010", ["TransID"] = "27AAACO0509P1ZB", ["TransName"] = "AKASH ROADWAYS" };
            if (tweak != null) tweak(r);
            return r;
        }

        [Fact]
        public void Standard_values_are_mapped_field_by_field()
        {
            var t = SapReader.MapStandardTransport(Row());
            Assert.Equal("27AAACO0509P1ZB", t.TransporterId); Assert.Equal("AKASH ROADWAYS", t.TransporterName); Assert.Equal("MH46H7010", t.VehicleNo);
            Assert.Equal("Road", t.Mode); Assert.Equal("Regular", t.VehicleType); Assert.Equal(1000m, t.Distance); Assert.Equal("", t.DocNo); Assert.Equal("", t.DocDate);
            Assert.Empty(SapReader.MissingTransportFields(t));
        }

        [Fact]
        public void Mode_vehicle_type_and_transport_document_codes_are_decoded()
        {
            Assert.Equal("Rail", SapReader.MapStandardTransport(Row(r => r["TransMode"] = 2)).Mode);
            Assert.Equal("Air", SapReader.MapStandardTransport(Row(r => r["TransMode"] = 3)).Mode);
            Assert.Equal("Ship", SapReader.MapStandardTransport(Row(r => r["TransMode"] = 4)).Mode);
            Assert.Equal("ODC", SapReader.MapStandardTransport(Row(r => r["VehicleTyp"] = "O")).VehicleType);
            var t = SapReader.MapStandardTransport(Row(r => { r["TransMode"] = 2; r["TransDocNo"] = "RR123"; r["TransDate"] = new DateTime(2026, 11, 24); }));
            Assert.Equal("RR123", t.DocNo); Assert.Equal("2026-11-24", t.DocDate);
            Assert.Empty(SapReader.MissingTransportFields(t));
        }

        [Fact]
        public void A_transporter_without_gstin_is_not_sent_as_fifteen_zeros()
        {
            Assert.Equal("", SapReader.MapStandardTransport(Row(r => r["TransID"] = "000000000000000")).TransporterId);
        }

        [Fact]
        public void An_empty_sap_form_stays_empty_and_is_reported_not_defaulted()
        {
            var t = SapReader.MapStandardTransport(Row(r => { r["TransMode"] = null; r["VehicleNo"] = null; r["VehicleTyp"] = null; r["TransID"] = null; r["TransName"] = null; r["Distance"] = 0m; }));
            Assert.Equal("", t.Mode); Assert.Equal("", t.VehicleNo);
            var m = SapReader.MissingTransportFields(t);
            Assert.Single(m); Assert.Contains("Transport Mode", m[0]); Assert.Contains("Electronic Documents", m[0]);
        }

        [Fact]
        public void Missing_vehicle_number_names_the_sap_field()
        {
            var m = SapReader.MissingTransportFields(SapReader.MapStandardTransport(Row(r => { r["VehicleNo"] = null; r["TransID"] = null; })));
            Assert.Contains(m, x => x.Contains("Vehicle Number") && x.Contains("Electronic Documents"));
            var v = SapReader.MissingTransportFields(SapReader.MapStandardTransport(Row(r => r["VehicleTyp"] = null)));
            Assert.Contains(v, x => x.Contains("Vehicle Type"));
            var rail = SapReader.MissingTransportFields(SapReader.MapStandardTransport(Row(r => r["TransMode"] = 2)));
            Assert.Contains(rail, x => x.Contains("Transport Document Number")); Assert.Contains(rail, x => x.Contains("Transport Document Date"));
        }

        [Fact]
        public void Invalid_distance_is_reported()
        {
            Assert.Contains(SapReader.MissingTransportFields(SapReader.MapStandardTransport(Row(r => r["Distance"] = 9999m))), x => x.Contains("Distance"));
        }

        [Fact]
        public void The_selected_invoice_docentry_is_the_only_one_read_and_the_values_are_not_changed()
        {
            var sql = new FakeSql { Row = Row() };
            var t = new SapReader(sql, "DB").GetStandardTransport(2627);
            Assert.Single(sql.Sql); Assert.Contains("FROM INV26 WHERE DocEntry=2627", sql.Sql[0]); Assert.StartsWith("SELECT", sql.Sql[0]);       // read-only, this invoice only
            Assert.Equal("MH46H7010", t.VehicleNo);
        }

        [Fact]
        public void A_missing_inv26_row_gives_an_empty_form_that_is_reported()
        {
            var t = new SapReader(new FakeSql(), "DB").GetStandardTransport(1);
            Assert.NotEmpty(SapReader.MissingTransportFields(t));
        }

        [Fact]
        public void The_gst_portal_payload_carries_the_mapped_sap_values()
        {
            var t = SapReader.MapStandardTransport(Row(r => r["TransID"] = "000000000000000"));
            var tr = InvoiceJsonBuilderTests.TransportJson(t);
            Assert.Equal("MH46H7010", (string)tr["vehicle_number"]); Assert.Equal("Road", (string)tr["transport_mode"]); Assert.Equal(1000m, (decimal)tr["distance"]);
            Assert.Equal("", (string)tr["transporter_id"]); Assert.Equal("Regular", (string)tr["vehicle_type"]); Assert.Equal("AKASH ROADWAYS", (string)tr["transporter_name"]);
        }

        [Fact]
        public void Portal_day_first_dates_are_read_correctly()
        {
            Assert.Equal(new DateTime(2026, 11, 24, 11, 50, 0), EInvGstAddon.ERPNext.GstGateway.ParseDt("24/11/2026 11:50:00 AM"));
            Assert.Equal(new DateTime(2026, 11, 25, 23, 59, 0), EInvGstAddon.ERPNext.GstGateway.ParseDt("25/11/2026 11:59:00 PM"));
            Assert.Equal(new DateTime(2026, 11, 25, 23, 59, 0), EInvGstAddon.ERPNext.GstGateway.ParseDt("2026-11-25 23:59:00"));
            Assert.Null(EInvGstAddon.ERPNext.GstGateway.ParseDt("not a date"));
        }

        [Fact]
        public void No_transport_entry_dialog_exists_in_the_e_way_bill_flow()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "EInvGstAddon.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string text = File.ReadAllText(Path.Combine(d.FullName, "src", "EInvGstAddon.UI", "InvoiceMenu.cs"));
            int a = text.IndexOf("private void GenerateEwb("); int b = text.IndexOf("private void ViewEwb(");
            Assert.True(a > 0 && b > a);
            string body = text.Substring(a, b - a);
            Assert.DoesNotContain("Dialogs.Input", body); Assert.DoesNotContain("Transporter Name", body); Assert.Contains("MissingTransportFields", body); Assert.Contains("GetStandardTransport(op.Ctx.DocEntry)", body);
        }
    }
}
