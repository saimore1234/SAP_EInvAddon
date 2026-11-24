using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EInvGstAddon.Core
{
    /// <summary>Runs a read-only SELECT against the SAP company database.</summary>
    public interface ISqlRunner
    {
        List<Dictionary<string, object>> Query(string sql);
    }

    /// <summary>Direct SQL Server runner (development / integration tests only; production uses the DI API Recordset).</summary>
    public sealed class SqlClientRunner : ISqlRunner
    {
        private readonly string _cs;
        public SqlClientRunner(string connectionString) { _cs = connectionString; }

        public List<Dictionary<string, object>> Query(string sql)
        {
            var rows = new List<Dictionary<string, object>>();
            using (var c = new SqlConnection(_cs))
            {
                c.Open();
                using (var cmd = new SqlCommand(sql, c))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                        rows.Add(d);
                    }
                }
            }
            return rows;
        }
    }

    /// <summary>
    /// Builds SAP DTOs with read-only SELECT statements. Never writes to SAP tables.
    /// The same code runs over the DI API Recordset (production) or SqlClient (tests).
    /// </summary>
    public sealed class SapReader : ISapSource
    {
        private readonly ISqlRunner _sql;
        private string _localCurrency;
        public string DatabaseName { get; private set; }

        public SapReader(ISqlRunner sql, string databaseName) { _sql = sql; DatabaseName = databaseName; }

        // ---------- helpers ----------
        public static string Lit(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }
        private static string S(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : ""; }
        private static decimal D(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : 0m; }
        private static int I(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static DateTime Dt(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToDateTime(v, CultureInfo.InvariantCulture) : DateTime.MinValue; }
        private Dictionary<string, object> One(string sql) { var l = _sql.Query(sql); return l.Count == 0 ? null : l[0]; }

        public string LocalCurrency
        {
            get
            {
                if (_localCurrency == null) { var r = One("SELECT MainCurncy FROM OADM"); _localCurrency = r == null ? "" : S(r, "MainCurncy"); }
                return _localCurrency;
            }
        }

        /// <summary>HSN codes in ERPNext are stored without dots (SAP: 3923.21.00 -> 39232100).</summary>
        public static string NormaliseHsn(string h) { return Regex.Replace(h ?? "", @"[^0-9]", ""); }

        // ---------- ISapSource ----------
        public SapInvoice GetInvoice(int docEntry)
        {
            var h = One("SELECT i.DocEntry,i.DocNum,i.ObjType,i.DocType,i.CANCELED,i.CardCode,i.CardName,i.DocDate,i.TaxDate,i.DocDueDate,i.DocCur,i.DocRate," +
                        "i.NumAtCard,i.Comments,i.PayToCode,i.ShipToCode,i.Address,i.Address2,i.ShipState,i.DiscPrcnt,i.DiscSum,i.DiscSumFC,i.TotalExpns,i.TotalExpFC," +
                        "i.RoundDif,i.RoundDifFC,i.DocTotal,i.DocTotalFC,i.VatSum,i.VatSumFC,t.PymntGroup,s.SlpName " +
                        "FROM OINV i LEFT JOIN OCTG t ON t.GroupNum=i.GroupNum LEFT JOIN OSLP s ON s.SlpCode=i.SlpCode WHERE i.DocEntry=" + docEntry);
            if (h == null) throw new InvalidOperationException("SAP A/R Invoice with DocEntry " + docEntry + " was not found.");

            var inv = new SapInvoice
            {
                DocEntry = I(h, "DocEntry"), DocNum = I(h, "DocNum"), ObjType = S(h, "ObjType"), Database = DatabaseName,
                DocType = S(h, "DocType"), Cancelled = S(h, "CANCELED") != "N",
                CardCode = S(h, "CardCode"), CardName = S(h, "CardName"),
                PostingDate = Dt(h, "DocDate"), DocumentDate = Dt(h, "TaxDate"), DueDate = Dt(h, "DocDueDate"),
                Currency = S(h, "DocCur"), ExchangeRate = D(h, "DocRate"),
                CustomerRef = S(h, "NumAtCard"), Remarks = S(h, "Comments"),
                PayToCode = S(h, "PayToCode"), ShipToCode = S(h, "ShipToCode"), BillToText = S(h, "Address"), ShipToText = S(h, "Address2"),
                PlaceOfSupplyState = S(h, "ShipState"), PaymentTermsName = S(h, "PymntGroup"), Salesperson = S(h, "SlpName"),
                HeaderDiscountPercent = D(h, "DiscPrcnt")
            };
            inv.ForeignCurrency = !string.Equals(inv.Currency, LocalCurrency, StringComparison.OrdinalIgnoreCase);
            if (inv.ExchangeRate <= 0) inv.ExchangeRate = 1;
            bool fc = inv.ForeignCurrency;
            inv.HeaderDiscount = fc ? D(h, "DiscSumFC") : D(h, "DiscSum");
            inv.Freight = fc ? D(h, "TotalExpFC") : D(h, "TotalExpns");
            inv.RoundOff = fc ? D(h, "RoundDifFC") : D(h, "RoundDif");
            inv.DocTotal = fc ? D(h, "DocTotalFC") : D(h, "DocTotal");
            inv.VatTotal = fc ? D(h, "VatSumFC") : D(h, "VatSum");

            foreach (var r in _sql.Query(
                "SELECT l.LineNum,l.ItemCode,l.Dscription,l.unitMsr,l.Quantity,l.InvQty,l.Price,l.PriceBefDi,l.DiscPrcnt,l.LineTotal,l.TotalFrgn,l.WhsCode,l.TaxCode,l.VatPrcnt,l.VatSum,l.VatSumFrgn," +
                "h.ChapterID AS Hsn,s.ServCode AS Sac,o.ItemName " +
                "FROM INV1 l LEFT JOIN OCHP h ON h.AbsEntry=l.HsnEntry LEFT JOIN OSAC s ON s.AbsEntry=l.SacEntry LEFT JOIN OITM o ON o.ItemCode=l.ItemCode " +
                "WHERE l.DocEntry=" + docEntry + " AND ISNULL(l.TreeType,'N')<>'I' ORDER BY l.LineNum"))
            {
                decimal price = D(r, "Price"), priceBefore = D(r, "PriceBefDi");
                if (priceBefore == 0) priceBefore = price;
                decimal qty = D(r, "Quantity");
                inv.Lines.Add(new SapInvoiceLine
                {
                    LineNum = I(r, "LineNum"), ItemCode = S(r, "ItemCode"), ItemName = S(r, "ItemName"), Description = S(r, "Dscription"),
                    Uom = S(r, "unitMsr"), Quantity = qty, UnitPrice = priceBefore, DiscountPercent = D(r, "DiscPrcnt"),
                    LineTotal = fc ? D(r, "TotalFrgn") : D(r, "LineTotal"), WarehouseCode = S(r, "WhsCode"), TaxCode = S(r, "TaxCode"),
                    VatPercent = D(r, "VatPrcnt"), VatAmount = fc ? D(r, "VatSumFrgn") : D(r, "VatSum"),
                    Hsn = NormaliseHsn(S(r, "Hsn")), Sac = S(r, "Sac")
                });
            }

            foreach (var r in _sql.Query("SELECT e.ExpnsCode,x.ExpnsName,e.LineTotal,e.TotalFrgn,e.TaxCode FROM INV3 e LEFT JOIN OEXD x ON x.ExpnsCode=e.ExpnsCode WHERE e.DocEntry=" + docEntry))
                inv.Expenses.Add(new SapFreight { ExpenseCode = I(r, "ExpnsCode"), Name = S(r, "ExpnsName"), Amount = fc ? D(r, "TotalFrgn") : D(r, "LineTotal"), TaxCode = S(r, "TaxCode") });

            foreach (var r in _sql.Query("SELECT LineNum,RelateType,staType,StaCode,TaxRate,TaxSum,TaxSumFrgn,BaseSum,BaseSumFrg FROM INV4 WHERE DocEntry=" + docEntry))
                inv.Taxes.Add(new SapTaxAmount
                {
                    LineNum = I(r, "LineNum"), RelateType = I(r, "RelateType"), TaxType = S(r, "staType"), TaxCode = S(r, "StaCode"),
                    Rate = D(r, "TaxRate"), Amount = fc ? D(r, "TaxSumFrgn") : D(r, "TaxSum"), Base = fc ? D(r, "BaseSumFrg") : D(r, "BaseSum")
                });
            return inv;
        }

        public SapPartner GetPartner(string cardCode)
        {
            var r = One("SELECT c.CardCode,c.CardName,c.CardType,g.GroupName,c.Currency,c.Phone1,c.Phone2,c.Cellular,c.E_Mail,c.CntctPrsn,c.BillToDef,c.ShipToDef,c.LicTradNum,c.Country,t.PymntGroup " +
                        "FROM OCRD c LEFT JOIN OCRG g ON g.GroupCode=c.GroupCode LEFT JOIN OCTG t ON t.GroupNum=c.GroupNum WHERE c.CardCode=" + Lit(cardCode));
            if (r == null) throw new InvalidOperationException("SAP business partner " + cardCode + " was not found.");
            var bp = new SapPartner
            {
                CardCode = S(r, "CardCode"), CardName = S(r, "CardName"), CardType = S(r, "CardType"), GroupName = S(r, "GroupName"),
                Currency = S(r, "Currency"), Phone1 = S(r, "Phone1"), Phone2 = S(r, "Phone2"), Cellular = S(r, "Cellular"), Email = S(r, "E_Mail"),
                ContactPerson = S(r, "CntctPrsn"), BillToDefault = S(r, "BillToDef"), ShipToDefault = S(r, "ShipToDef"), PaymentTerms = S(r, "PymntGroup"), Country = S(r, "Country")
            };
            // PAN is held in the India localisation field CRD7.TaxId0. Only a syntactically valid PAN is used; nothing is invented.
            var pan = One("SELECT TOP 1 TaxId0 FROM CRD7 WHERE CardCode=" + Lit(cardCode) + " AND ISNULL(TaxId0,'')<>'' ORDER BY CASE WHEN ISNULL(Address,'')='' THEN 0 ELSE 1 END");
            string p = pan == null ? "" : S(pan, "TaxId0").ToUpperInvariant();
            bp.Pan = Regex.IsMatch(p, "^[A-Z]{5}[0-9]{4}[A-Z]$") ? p : "";
            return bp;
        }

        public SapAddress GetAddress(string cardCode, string addressCode, string type)
        {
            if (string.IsNullOrEmpty(addressCode)) return null;
            var r = One("SELECT a.Address,a.AdresType,a.Street,a.Block,a.Building,a.StreetNo,a.City,a.ZipCode,a.State,a.Country,a.Address2,a.Address3,a.GSTRegnNo,a.GSTType,st.Name AS StName,cy.Name AS CyName " +
                        "FROM CRD1 a LEFT JOIN OCST st ON st.Code=a.State AND st.Country=a.Country LEFT JOIN OCRY cy ON cy.Code=a.Country " +
                        "WHERE a.CardCode=" + Lit(cardCode) + " AND a.Address=" + Lit(addressCode) + " AND a.AdresType=" + Lit(type));
            if (r == null) return null;
            string gstin = S(r, "GSTRegnNo").ToUpperInvariant();
            return new SapAddress
            {
                Code = S(r, "Address"), Type = S(r, "AdresType"), Street = S(r, "Street"), Block = S(r, "Block"), Building = S(r, "Building"), StreetNo = S(r, "StreetNo"),
                City = S(r, "City"), ZipCode = S(r, "ZipCode"), StateCode = S(r, "State"), StateName = S(r, "StName"), Country = S(r, "CyName"),
                Line2 = S(r, "Address2"), Line3 = S(r, "Address3"),
                Gstin = Regex.IsMatch(gstin, @"^[0-9]{2}[A-Z0-9]{10}[0-9A-Z]{3}$") ? gstin : "", GstType = I(r, "GSTType")
            };
        }

        public SapItem GetItem(string itemCode)
        {
            var r = One("SELECT i.ItemCode,i.ItemName,i.FrgnName,g.ItmsGrpNam,i.InvntItem,i.SellItem,i.PrchseItem,i.InvntryUom,i.SalUnitMsr,i.DfltWH,h.ChapterID AS Hsn,s.ServCode AS Sac " +
                        "FROM OITM i LEFT JOIN OITB g ON g.ItmsGrpCod=i.ItmsGrpCod LEFT JOIN OCHP h ON h.AbsEntry=i.ChapterID LEFT JOIN OSAC s ON s.AbsEntry=i.SACEntry WHERE i.ItemCode=" + Lit(itemCode));
            if (r == null) throw new InvalidOperationException("SAP item " + itemCode + " was not found.");
            return new SapItem
            {
                ItemCode = S(r, "ItemCode"), ItemName = S(r, "ItemName"), ForeignName = S(r, "FrgnName"), GroupName = S(r, "ItmsGrpNam"),
                Inventory = S(r, "InvntItem") == "Y", Sales = S(r, "SellItem") == "Y", Purchase = S(r, "PrchseItem") == "Y",
                InventoryUom = S(r, "InvntryUom"), SalesUom = S(r, "SalUnitMsr"), DefaultWarehouse = S(r, "DfltWH"),
                Hsn = NormaliseHsn(S(r, "Hsn")), Sac = S(r, "Sac")
            };
        }

        public SapWarehouse GetWarehouse(string whsCode)
        {
            var r = One("SELECT WhsCode,WhsName,Street,City,ZipCode,State FROM OWHS WHERE WhsCode=" + Lit(whsCode));
            if (r == null) throw new InvalidOperationException("SAP warehouse " + whsCode + " was not found.");
            return ToWhs(r);
        }

        private static SapWarehouse ToWhs(Dictionary<string, object> r)
        {
            return new SapWarehouse { Code = S(r, "WhsCode"), Name = S(r, "WhsName"), Street = S(r, "Street"), City = S(r, "City"), ZipCode = S(r, "ZipCode"), State = S(r, "State") };
        }

        public List<SapWarehouse> ListWarehouses()
        {
            var list = new List<SapWarehouse>();
            foreach (var r in _sql.Query("SELECT WhsCode,WhsName,Street,City,ZipCode,State FROM OWHS ORDER BY WhsCode")) list.Add(ToWhs(r));
            return list;
        }

        public Dictionary<string, object> Query1(string sql) { return One(sql); }

        /// <summary>Transport details the user already entered in SAP's standard E-Way Bill form (table INV26); empty values stay empty. Read-only.</summary>
        public TransportInput GetStandardTransport(int docEntry)
        {
            var t = new TransportInput();
            var r = One("SELECT TransID,TransName,TransMode,VehicleNo,VehicleTyp,Distance,TransDocNo,TransDate FROM INV26 WHERE DocEntry=" + docEntry);
            if (r == null) return t;
            t.TransporterId = S(r, "TransID"); t.TransporterName = S(r, "TransName"); t.VehicleNo = S(r, "VehicleNo"); t.DocNo = S(r, "TransDocNo"); t.Distance = D(r, "Distance");
            switch (S(r, "TransMode")) { case "2": t.Mode = "Rail"; break; case "3": t.Mode = "Air"; break; case "4": t.Mode = "Ship"; break; default: t.Mode = "Road"; break; }
            t.VehicleType = S(r, "VehicleTyp") == "O" ? "ODC" : "Regular";
            object d; DateTime dt;
            if (r.TryGetValue("TransDate", out d) && d != null && DateTime.TryParse(Convert.ToString(d, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) t.DocDate = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return t;
        }

        // ---------- GST source (everything the GST invoice JSON needs; read-only) ----------
        public GstSource GetGstSource(int docEntry)
        {
            var h = One("SELECT i.DocEntry,i.DocNum,i.Series,n.SeriesName,i.ObjType,i.DocType,i.CANCELED,i.CardCode,i.CardName,i.DocDate,i.DocDueDate,i.DocCur,i.DocRate,i.GSTTranTyp,i.NumAtCard,i.PayToCode,i.ShipToCode,i.ShipState," +
                        "i.DiscPrcnt,i.DiscSum,i.TotalExpns,i.RoundDif,i.DocTotal,i.DocTotalFC FROM OINV i LEFT JOIN NNM1 n ON n.Series=i.Series WHERE i.DocEntry=" + docEntry);
            if (h == null) throw new InvalidOperationException("SAP A/R Invoice with DocEntry " + docEntry + " was not found.");
            var s = new GstSource
            {
                DocEntry = I(h, "DocEntry"), DocNum = I(h, "DocNum"), Series = I(h, "Series"), SeriesName = S(h, "SeriesName"), ObjType = S(h, "ObjType"), DocType = S(h, "DocType"), Database = DatabaseName,
                Cancelled = S(h, "CANCELED") != "N", CardCode = S(h, "CardCode"), CardName = S(h, "CardName"), PostingDate = Dt(h, "DocDate"), DueDate = Dt(h, "DocDueDate"), DocCurrency = S(h, "DocCur"),
                ExchangeRate = D(h, "DocRate") <= 0 ? 1 : D(h, "DocRate"), GstTransactionType = S(h, "GSTTranTyp"), CustomerRef = S(h, "NumAtCard"), PayToCode = S(h, "PayToCode"), ShipToCode = S(h, "ShipToCode"), ShipStateCode = S(h, "ShipState"),
                HeaderDiscountPercent = D(h, "DiscPrcnt"), HeaderDiscountLc = D(h, "DiscSum"), FreightLc = D(h, "TotalExpns"), RoundOffLc = D(h, "RoundDif"), DocTotalLc = D(h, "DocTotal"), DocTotalFc = D(h, "DocTotalFC")
            };
            s.ForeignCurrency = !string.Equals(s.DocCurrency, LocalCurrency, StringComparison.OrdinalIgnoreCase);

            s.Buyer = ReadParty(s.CardCode, s.PayToCode, "B");
            s.Buyer.Code = s.CardCode; if (string.IsNullOrEmpty(s.Buyer.Name)) s.Buyer.Name = s.CardName;
            if (!string.IsNullOrEmpty(s.ShipToCode) && s.ShipToCode != s.PayToCode)
            {
                s.ShipTo = ReadParty(s.CardCode, s.ShipToCode, "S"); s.ShipTo.Name = s.ShipTo.Name ?? s.CardName;
                if (string.IsNullOrEmpty(s.ShipTo.Name)) s.ShipTo.Name = s.CardName;
            }

            foreach (var r in _sql.Query("SELECT l.LineNum,l.ItemCode,l.Dscription,l.unitMsr,l.Quantity,l.Price,l.PriceBefDi,l.DiscPrcnt,l.LineTotal,h.ChapterID AS Hsn,sc.ServCode AS Sac " +
                                         "FROM INV1 l LEFT JOIN OCHP h ON h.AbsEntry=l.HsnEntry LEFT JOIN OSAC sc ON sc.AbsEntry=l.SacEntry WHERE l.DocEntry=" + docEntry + " AND ISNULL(l.TreeType,'N')<>'I' ORDER BY l.LineNum"))
                s.Lines.Add(new GstLine { LineNum = I(r, "LineNum"), ItemCode = S(r, "ItemCode"), Description = S(r, "Dscription"), Uom = S(r, "unitMsr"), Quantity = D(r, "Quantity"), UnitPrice = D(r, "PriceBefDi") != 0 ? D(r, "PriceBefDi") : D(r, "Price"),
                    DiscountPercent = D(r, "DiscPrcnt"), LineTotalLc = D(r, "LineTotal"), Hsn = NormaliseHsn(S(r, "Hsn")), Sac = S(r, "Sac") });
            foreach (var r in _sql.Query("SELECT LineNum,RelateType,staType,StaCode,TaxRate,TaxSum,BaseSum FROM INV4 WHERE DocEntry=" + docEntry))
                s.Taxes.Add(new GstTaxRow { LineNum = I(r, "LineNum"), RelateType = I(r, "RelateType"), TaxType = S(r, "staType"), TaxCode = S(r, "StaCode"), Rate = D(r, "TaxRate"), AmountLc = D(r, "TaxSum"), BaseLc = D(r, "BaseSum") });
            foreach (var r in _sql.Query("SELECT e.ExpnsCode,x.ExpnsName,e.LineTotal FROM INV3 e LEFT JOIN OEXD x ON x.ExpnsCode=e.ExpnsCode WHERE e.DocEntry=" + docEntry))
                s.Expenses.Add(new GstExpense { ExpenseCode = I(r, "ExpnsCode"), Name = S(r, "ExpnsName"), AmountLc = D(r, "LineTotal") });
            return s;
        }

        private GstParty ReadParty(string cardCode, string addressCode, string type)
        {
            var p = new GstParty();
            var r = One("SELECT a.Address,a.Street,a.Block,a.Building,a.StreetNo,a.City,a.ZipCode,a.State,a.Country,a.Address2,a.GSTRegnNo,cy.ISO2Code FROM CRD1 a LEFT JOIN OCRY cy ON cy.Code=a.Country " +
                        "WHERE a.CardCode=" + Lit(cardCode) + " AND a.Address=" + Lit(addressCode) + " AND a.AdresType=" + Lit(type));
            if (r == null) return p;
            string g = S(r, "GSTRegnNo").ToUpperInvariant();
            p.Name = null; p.Street = S(r, "Street"); p.Block = S(r, "Block"); p.Building = S(r, "Building"); p.StreetNo = S(r, "StreetNo"); p.Address2 = S(r, "Address2"); p.City = S(r, "City"); p.ZipCode = S(r, "ZipCode");
            p.StateCode = S(r, "State"); p.CountryCode = S(r, "Country"); p.CountryIso2 = !string.IsNullOrEmpty(S(r, "ISO2Code")) ? S(r, "ISO2Code") : (S(r, "Country").Length == 2 ? S(r, "Country") : "");
            // (SAP country codes are ISO alpha-2 when OCRY.ISO2Code is not filled)
            p.Gstin = Regex.IsMatch(g, @"^[0-9]{2}[A-Z0-9]{10}[0-9A-Z]{3}$") ? g : "";
            var n = One("SELECT CardName FROM OCRD WHERE CardCode=" + Lit(cardCode)); if (n != null) p.Name = S(n, "CardName");
            return p;
        }

        public int GetMaxDocEntry()
        {
            var r = One("SELECT ISNULL(MAX(DocEntry),0) AS M FROM OINV");
            return r == null ? 0 : I(r, "M");
        }

        public List<int> ListUnrecordedInvoices(int fromDocEntry)
        {
            var l = new List<int>();
            foreach (var r in _sql.Query("SELECT i.DocEntry FROM OINV i WHERE i.DocEntry>=" + fromDocEntry + " AND i.CANCELED='N' AND ISNULL(i.U_" + Common.Constants.UdfErpStatus + ",'')='' ORDER BY i.DocEntry"))
                l.Add(I(r, "DocEntry"));
            return l;
        }
    }
}
