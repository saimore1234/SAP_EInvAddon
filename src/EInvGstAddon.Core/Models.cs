using System;
using System.Collections.Generic;

namespace EInvGstAddon.Core
{
    public sealed class SapAddress
    {
        public string Code;          // CRD1.Address (address id)
        public string Type;          // "B" bill-to / "S" ship-to
        public string Street, Block, Building, StreetNo, City, ZipCode, StateCode, StateName, Country, Line2, Line3;
        public string Gstin;         // CRD1.GSTRegnNo
        public int GstType;          // CRD1.GSTType (0 = not set)
        public string Text;          // free-text address on the document
    }

    public sealed class SapPartner
    {
        public string CardCode, CardName, CardType, GroupName, Currency;
        public string Pan, Phone1, Phone2, Cellular, Email, ContactPerson, PaymentTerms;
        public string Country;
        public string BillToDefault, ShipToDefault;
        public List<SapAddress> Addresses = new List<SapAddress>();
    }

    public sealed class SapItem
    {
        public string ItemCode, ItemName, ForeignName, GroupName;
        public bool Inventory, Sales, Purchase;
        public string InventoryUom, SalesUom;
        public string Hsn;           // OCHP.ChapterID (HSN) if set
        public string Sac;           // OSAC code if set
        public string DefaultWarehouse;
    }

    public sealed class SapWarehouse
    {
        public string Code, Name, Street, City, ZipCode, State;
    }

    public sealed class SapTaxAmount
    {
        public int LineNum;          // -1 for expense / header-level taxes
        public string TaxType;       // INV4.staType as text, e.g. -100 (CGST), -110 (SGST), -120 (IGST)
        public string TaxCode;       // INV4.StaCode
        public decimal Rate;
        public decimal Amount;       // document currency
        public decimal Base;
        public int RelateType;       // 1 = item row, 3 = freight/expense row
    }

    public sealed class SapInvoiceLine
    {
        public int LineNum;
        public string ItemCode, ItemName, Description, Uom, WarehouseCode, TaxCode, Hsn, Sac;
        public decimal Quantity, UnitPrice, DiscountPercent, LineTotal, VatPercent, VatAmount;
    }

    public sealed class SapFreight
    {
        public int ExpenseCode;
        public string Name;
        public decimal Amount;
        public string TaxCode;
    }

    public sealed class SapInvoice
    {
        public int DocEntry, DocNum;
        public string ObjType = "13";
        public string Database;
        public string DocType;           // I = item, S = service
        public bool Cancelled;
        public string CardCode, CardName;
        public DateTime PostingDate, DocumentDate, DueDate;
        public string Currency;          // SAP currency code
        public bool ForeignCurrency;
        public decimal ExchangeRate = 1;
        public string PaymentTermsName;
        public string Salesperson;
        public string Remarks, CustomerRef;
        public string PayToCode, ShipToCode, BillToText, ShipToText;
        public string PlaceOfSupplyState;    // OINV.ShipState (SAP state code)
        public decimal HeaderDiscountPercent, HeaderDiscount, Freight, RoundOff, DocTotal, VatTotal;
        public List<SapInvoiceLine> Lines = new List<SapInvoiceLine>();
        public List<SapFreight> Expenses = new List<SapFreight>();
        public List<SapTaxAmount> Taxes = new List<SapTaxAmount>();

        public string SyncReference { get { return Database + "-" + ObjType + "-" + DocEntry; } }
    }

    public sealed class SyncRecord
    {
        public int DocEntry, DocNum;
        public string Database, ErpInvoice, CustomerRef, Status, Error;
        public DateTime? SyncDate;
        public int RetryCount;
        public string Code;              // UDT row code
        public DateTime? NextAttempt;    // in-memory only
    }

    public sealed class MapEntry
    {
        public string SapKey, ErpName, Created, Note;   // Created = "Y" when the add-on created the ERPNext master
    }

    /// <summary>Outcome of one synchronisation attempt, shown in SAP.</summary>
    public sealed class SyncOutcome
    {
        public bool Success;
        public string Status;
        public string ErpInvoice, Message;
        public bool CustomerCreated;
        public int ItemsCreated;
        public List<string> Warnings = new List<string>();
        public string ErpDocStatus;
        public decimal ErpGrandTotal;
    }
}
