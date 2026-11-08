using EInvGstAddon.Common;

namespace EInvGstAddon.Tests
{
    /// <summary>Test data only (a fictional configuration); the product code contains no customer values.</summary>
    public static class TestCfg
    {
        public static AppConfig Make()
        {
            var c = new AppConfig
            {
                Id = "t1", Name = "Test", SapDatabase = "Vipra", ErpUrl = "https://erp.example.test", ApiKey = "key123456",
                ErpCompany = "Vipra Closures Private Limited", ErpAbbr = "VCPL", ReferenceIncludesConfigId = false,
                FreightAccount = "Freight and Forwarding Charges - VCPL", RoundOffAccount = "Rounded Off - VCPL", SalesInvoiceSeries = "VCPL-SINV-.YYYY.-.#####",
                DefaultCustomerGroup = "Commercial", DefaultItemGroup = "Products"
            };
            c.ApiSecret = "secret123456";
            c.TaxAccountMap["-100"] = "Output Tax CGST - VCPL"; c.TaxAccountMap["-110"] = "Output Tax SGST - VCPL"; c.TaxAccountMap["-120"] = "Output Tax IGST - VCPL";
            c.CurrencyMap["`"] = "INR";
            return c;
        }
    }
}
