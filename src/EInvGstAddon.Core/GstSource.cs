using System;
using System.Collections.Generic;

namespace EInvGstAddon.Core
{
    /// <summary>Everything read from SAP (through the DI API) that the GST invoice JSON needs. Amounts are LOCAL currency (GST is always reported in INR).</summary>
    public sealed class GstParty
    {
        public string Code, Name, Street, Block, Building, StreetNo, Address2, City, ZipCode, StateCode, StateName, CountryCode, CountryIso2, Gstin;
    }

    public sealed class GstLine
    {
        public int LineNum;
        public string ItemCode, Description, Uom, Hsn, Sac;
        public decimal Quantity, UnitPrice, DiscountPercent, LineTotalLc;
    }

    public sealed class GstTaxRow
    {
        public int LineNum, RelateType;         // RelateType 1 = item row, 3 = freight/expense row
        public string TaxType, TaxCode;         // INV4.staType / StaCode
        public decimal Rate, AmountLc, BaseLc;
    }

    public sealed class GstExpense
    {
        public int ExpenseCode; public string Name; public decimal AmountLc;
    }

    public sealed class GstSource
    {
        public int DocEntry, DocNum, Series;
        public string SeriesName, ObjType = "13", DocType, Database, CardCode, CardName, DocCurrency, GstTransactionType, CustomerRef, PayToCode, ShipToCode, ShipStateCode;
        public bool Cancelled;
        public DateTime PostingDate, DueDate;
        public decimal ExchangeRate = 1, HeaderDiscountPercent, HeaderDiscountLc, FreightLc, RoundOffLc, DocTotalLc, DocTotalFc;
        public bool ForeignCurrency;
        public GstParty Buyer = new GstParty();
        public GstParty ShipTo;                 // only when different from the bill-to address
        public GstParty EwbShipTo;              // SAP E-Way Bill details > Ship To (INV26.To*): the actual delivery point; for exports a place INSIDE India (port / airport / warehouse)
        public string ShipSnapCountry = "", ShipSnapState = "", ShipSnapZip = "";   // the Ship-To address saved on the invoice itself (INV12.CountryS/StateS/ZipCodeS); may differ from the customer master
        public List<GstLine> Lines = new List<GstLine>();
        public List<GstTaxRow> Taxes = new List<GstTaxRow>();
        public List<GstExpense> Expenses = new List<GstExpense>();
    }

    /// <summary>Transport values entered by the user when an E-Way Bill is generated (not stored in SAP invoices).</summary>
    public sealed class TransportInput
    {
        public string TransporterId = "", TransporterName = "", VehicleNo = "", Mode = "Road", VehicleType = "Regular", DocNo = "", DocDate = "";
        public decimal Distance;
    }

    /// <summary>Standard GST state codes by the postal abbreviations used in SAP India. Used only when the buyer GSTIN does not already tell the state.</summary>
    public static class IndiaStates
    {
        public static readonly Dictionary<string, string> ByAbbreviation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "JK", "01" }, { "HP", "02" }, { "PB", "03" }, { "CH", "04" }, { "UK", "05" }, { "UA", "05" }, { "HR", "06" }, { "DL", "07" }, { "RJ", "08" }, { "UP", "09" }, { "BR", "10" },
            { "SK", "11" }, { "AR", "12" }, { "NL", "13" }, { "MN", "14" }, { "MZ", "15" }, { "TR", "16" }, { "ML", "17" }, { "AS", "18" }, { "WB", "19" }, { "JH", "20" }, { "OR", "21" },
            { "OD", "21" }, { "CG", "22" }, { "CT", "22" }, { "MP", "23" }, { "GJ", "24" }, { "DD", "26" }, { "DN", "26" }, { "DNH", "26" }, { "MH", "27" }, { "KA", "29" }, { "GA", "30" },
            { "LD", "31" }, { "KL", "32" }, { "TN", "33" }, { "PY", "34" }, { "PO", "34" }, { "AN", "35" }, { "TS", "36" }, { "TG", "36" }, { "AP", "37" }, { "LA", "38" }
        };
    }
}
