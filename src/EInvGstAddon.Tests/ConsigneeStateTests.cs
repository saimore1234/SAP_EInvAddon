using System.Linq;
using EInvGstAddon.Core;
using Xunit;

namespace EInvGstAddon.Tests
{
    /// <summary>E-Way Bill consignee (Ship-To) state: taken from the Ship-To address saved on the invoice, never defaulted, foreign consignees not forced into an Indian state.</summary>
    public class ConsigneeStateTests
    {
        private static readonly TransportInput Tr = new TransportInput { Mode = "Road", VehicleNo = "MH04AB1234", VehicleType = "Regular", Distance = 100 };

        private static BuildResult Build(GstSource s) { return InvoiceJsonBuilder.Build(s, InvoiceJsonBuilderTests.Cfg(), Tr); }
        private static bool HasConsigneeError(BuildResult r) { return r.Errors.Any(e => e.Contains("Consignee Ship-To State Code") || e.Contains("Ship-To state")); }

        [Fact]
        public void Valid_indian_ship_to_state_passes()
        {
            var r = Build(InvoiceJsonBuilderTests.Src());
            Assert.False(HasConsigneeError(r), string.Join(";", r.Errors));
        }

        [Fact]
        public void Missing_ship_to_state_gives_the_specific_message_and_nothing_is_defaulted()
        {
            var s = InvoiceJsonBuilderTests.Src(); s.Buyer.StateCode = ""; s.Buyer.Gstin = "";
            var r = Build(s);
            Assert.Contains("Consignee Ship-To State Code is missing or invalid. Please check the Ship-To address in the SAP Business One invoice.", r.Errors);
        }

        [Fact]
        public void A_state_without_a_gst_code_mapping_is_reported_separately()
        {
            var s = InvoiceJsonBuilderTests.Src(); s.ShipSnapCountry = "IN"; s.ShipSnapState = "ZZ"; s.Buyer.Gstin = "";
            var r = Build(s);
            Assert.Contains(r.Errors, e => e.Contains("'ZZ'") && e.Contains("no GST state code mapping"));
            Assert.DoesNotContain("Consignee Ship-To State Code is missing or invalid. Please check the Ship-To address in the SAP Business One invoice.", r.Errors);
        }

        [Theory]
        [InlineData("MH", "27")]
        [InlineData("KA", "29")]
        [InlineData("GJ", "24")]
        [InlineData("dl", "07")]
        public void State_abbreviations_map_to_gst_state_codes(string abbreviation, string code)
        {
            string c;
            Assert.True(IndiaStates.ByAbbreviation.TryGetValue(abbreviation, out c)); Assert.Equal(code, c);
            var s = InvoiceJsonBuilderTests.Src(); s.ShipSnapCountry = "IN"; s.ShipSnapState = abbreviation; s.Buyer.Gstin = "";
            Assert.False(HasConsigneeError(Build(s)));
        }

        [Fact]
        public void Supplier_and_consignee_in_different_states_are_both_kept()
        {
            var s = InvoiceJsonBuilderTests.Src(); s.ShipSnapCountry = "IN"; s.ShipSnapState = "GJ"; s.Buyer.Gstin = "";
            var r = Build(s);
            Assert.False(HasConsigneeError(r));
            Assert.NotEqual((string)r.Json["seller"]["state_code"], "24");
        }

        [Fact]
        public void The_state_saved_on_the_invoice_wins_over_the_customer_master()
        {
            var s = InvoiceJsonBuilderTests.Src();                      // customer master (Buyer) says KA, a valid state
            s.Buyer.Gstin = ""; s.ShipSnapCountry = "IN"; s.ShipSnapState = "ZZ";   // but the invoice's saved Ship-To has an unknown state
            Assert.True(HasConsigneeError(Build(s)));                   // the master is not silently substituted
            s.ShipSnapState = "GJ";
            Assert.False(HasConsigneeError(Build(s)));
        }

        [Fact]
        public void A_missing_place_of_supply_does_not_hide_or_replace_the_consignee_state()
        {
            var s = InvoiceJsonBuilderTests.Src(); s.ShipStateCode = null; s.Buyer.StateCode = ""; s.Buyer.Gstin = "";
            Assert.True(HasConsigneeError(Build(s)));
        }

        [Fact]
        public void A_foreign_ship_to_is_not_forced_into_an_indian_state()
        {
            var s = InvoiceJsonBuilderTests.Src(); s.Buyer.CountryIso2 = "PH"; s.Buyer.StateCode = "MAN"; s.Buyer.Gstin = ""; s.ShipSnapCountry = "PH"; s.ShipSnapState = "MAN";
            var r = Build(s);
            Assert.False(HasConsigneeError(r), string.Join(";", r.Errors));
            Assert.Equal("EXPWOP", (string)r.Json["supply_type"]);
        }

        [Fact]
        public void Invoice_73_database_values_are_handled_as_an_export_to_the_philippines()
        {
            // A/R Invoice DocNum 73 (DocEntry 2630, TTC database), values read from SAP: INV12.CountryS=PH, StateS=MAN, ZipCodeS=999999, no GSTIN; INV26.ToState=99, ToGSTN=URP; tax IGST@28.
            var s = InvoiceJsonBuilderTests.Src();
            s.DocEntry = 2630; s.DocNum = 73; s.CardCode = "CF00231"; s.CardName = "GINEBRA SAN MIGUEL";
            s.Buyer = new GstParty { Code = "CF00231", Name = "GINEBRA SAN MIGUEL", Street = "1555 CITY OF MANDALUYONG NCR", City = "CITY OF MANDALUYONG NCR", ZipCode = "999999", StateCode = "MAN", CountryIso2 = "PH", Gstin = "" };
            s.ShipSnapCountry = "PH"; s.ShipSnapState = "MAN"; s.ShipSnapZip = "999999"; s.ShipStateCode = null;
            var r = Build(s);
            Assert.False(HasConsigneeError(r), string.Join(";", r.Errors));
            Assert.Equal("URP", (string)r.Json["buyer"]["gstin"]); Assert.Equal("96", (string)r.Json["place_of_supply"]);     // e-Invoice codes; the e-Way Bill payload maps them to 99 server-side
        }
    }
}
