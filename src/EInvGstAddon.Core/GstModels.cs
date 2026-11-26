using System;
using System.Collections.Generic;

namespace EInvGstAddon.Core
{
    /// <summary>Live GST state of one ERPNext Sales Invoice, read from ERPNext / india_compliance (never computed locally).</summary>
    public sealed class GstStatus
    {
        public string SalesInvoice, Company, Customer, CustomerName, CompanyGstin, BillingGstin, PlaceOfSupply;
        public int DocStatus;
        public DateTime? PostingDate;
        public decimal NetTotal, TaxTotal, GrandTotal;
        public bool EInvoiceUnknown, EwbUnknown;     // server reported "Unknown / Reconciliation Required"
        public string LastMessage;

        // e-Invoice
        public string Irn, EInvoiceStatus, AckNo, SignedQr, SignedInvoice;
        public DateTime? AckDate;
        public bool IrnCancelled;
        public DateTime? IrnCancelledOn;

        // e-Waybill
        public string EwbNo, EwbStatus;
        public DateTime? EwbDate, EwbValidUpto;
        public bool EwbCancelled;
        public string VehicleNo, TransporterId, TransporterName, ModeOfTransport, VehicleType, LrNo, LrDate;
        public decimal Distance;

        public bool HasIrn { get { return !string.IsNullOrEmpty(Irn); } }
        public bool HasEwb { get { return !string.IsNullOrEmpty(EwbNo) && !EwbCancelled; } }
        public string DocStatusText { get { return DocStatus == 0 ? "Draft" : DocStatus == 1 ? "Submitted" : "Cancelled"; } }
    }

    /// <summary>Result of a GST pre-check: Errors block the action, Warnings are shown but do not.</summary>
    public sealed class GstCheck
    {
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public bool Ok { get { return Errors.Count == 0; } }
        public override string ToString() { return string.Join("\n", Errors) + (Warnings.Count > 0 ? "\n(" + string.Join("; ", Warnings) + ")" : ""); }
    }

    public sealed class EWayBillInput
    {
        public string TransporterId, TransporterName, VehicleNo, ModeOfTransport = "Road", VehicleType = "Regular", LrNo, LrDate;
        public decimal Distance;
    }

    /// <summary>GST data returned to SAP for one invoice (stored in the add-on's UDT; large payloads only in memo fields).</summary>
    public sealed class GstRecord
    {
        public int DocEntry, DocNum;
        public string ErpInvoice, EInvStatus, Irn, AckNo, AckDate, SignedQr, SignedInvoice, CancelStatus, Error, QrPath;
        public string EwbStatus, EwbNo, EwbDate, EwbValid, Transporter, Vehicle, Mode;
        public decimal Distance, Taxable, Gst;
        public string Customer, Gstin;
        public DateTime? InvoiceDate;
        public DateTime? LastSync;
    }

    /// <summary>One GST operation in the SAP-side integration history (@EINV_TXN).</summary>
    public sealed class GstHistory
    {
        public int DocEntry;
        public string Operation, Status, RequestId, Reference, Message;
        public DateTime At;
    }

    public interface IGstStore
    {
        GstRecord GetGst(int docEntry);
        void SaveGst(GstRecord rec);
        void AddHistory(GstHistory h);
        System.Collections.Generic.List<GstHistory> ListHistory(int docEntry, int max);
    }
}
