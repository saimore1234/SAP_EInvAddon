namespace EInvGstAddon.Common
{
    public static class Constants
    {
        public const string AddonName = "EInvGstAddon";
        /// <summary>SAP B1 object type of an A/R Invoice.</summary>
        public const string ArInvoiceObjType = "13";

        // UDFs on OINV (aliases without the U_ prefix)
        public const string UdfErpDoc = "ERPNextDoc";
        public const string UdfErpStatus = "ERPNextStatus";
        public const string UdfLastSync = "LastSync";
        public const string UdfSyncError = "SyncError";

        // UDTs (without the @ prefix)
        public const string UdtSync = "EINV_SYNC";
        public const string UdtCustMap = "EINV_CUSTMAP";
        public const string UdtItemMap = "EINV_ITEMMAP";
        public const string UdtWhsMap = "EINV_WHSMAP";
        public const string UdtDocs = "EINV_DOCS";

        // GST UDFs on OINV
        public const string UdfEInvStatus = "EInvStatus";
        public const string UdfIrn = "IRN";
        public const string UdfAckNo = "AckNo";
        public const string UdfAckDate = "AckDate";
        public const string UdfEwbStatus = "EWBStatus";
        public const string UdfEwbNo = "EWBNo";
        public const string UdfEwbDate = "EWBDate";
        public const string UdfEwbValid = "EWBValid";
        public const string UdfCancelStatus = "CancelStatus";
        public const string UdfSignedQr = "SignedQR";      // memo on OINV: the signed QR text, so a Crystal report needs no join
        public const string UdfQrPath = "QRPath";          // full path of the saved QR image (PNG) for Crystal's dynamic image location
        public const string UdfGstStatus = "GSTStatus";
        public const string UdfGstSyncDate = "GSTSyncDate";
        public const string UdfGstError = "GSTError";
        public const string UdfErpRequestId = "ERPRequestID";
        public const string UdtTxn = "EINV_TXN";
    }

    public static class SyncStatus
    {
        public const string Pending = "Pending";
        public const string Processing = "Processing";
        public const string Synced = "Synced";
        public const string Failed = "Failed";
        public const string RetryRequired = "Retry Required";
    }
}
