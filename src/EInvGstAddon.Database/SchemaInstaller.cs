using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using SAPbobsCOM;

namespace EInvGstAddon.Database
{
    /// <summary>Creates the add-on's UDFs and UDTs through the DI API. Idempotent: existing objects are left alone.</summary>
    public sealed class SchemaInstaller
    {
        private readonly DiSession _di;
        public SchemaInstaller(DiSession di) { _di = di; }

        private struct Fld
        {
            public string Name, Desc; public BoFieldTypes Type; public int Size; public BoFldSubTypes Sub;
            public Fld(string n, string d, BoFieldTypes t, int s = 0, BoFldSubTypes sub = BoFldSubTypes.st_None) { Name = n; Desc = d; Type = t; Size = s; Sub = sub; }
        }

        private static Fld A(string n, string d, int size) { return new Fld(n, d, BoFieldTypes.db_Alpha, size); }

        public List<string> Install()
        {
            var log = new List<string>();
            // --- UDTs ---
            EnsureTable(Constants.UdtSync, "GST Portal invoice sync", log);
            EnsureTable(Constants.UdtCustMap, "GST Portal customer map", log);
            EnsureTable(Constants.UdtItemMap, "GST Portal item map", log);
            EnsureTable(Constants.UdtWhsMap, "GST Portal warehouse map", log);

            EnsureFields("@" + Constants.UdtSync, new[]
            {
                new Fld("DocEntry", "SAP DocEntry", BoFieldTypes.db_Numeric, 11),
                new Fld("DocNum", "SAP DocNum", BoFieldTypes.db_Numeric, 11),
                A("ConfigId", "Configuration Id", 20), A("Database", "SAP Database", 30), A("ErpInv", "GST Portal Invoice ID", 60), A("Customer", "Customer Mapping", 140),
                A("Status", "Sync Status", 20), A("SyncTs", "Sync Date/Time", 20), A("Error", "Error Message", 254),
                new Fld("Retry", "Retry Count", BoFieldTypes.db_Numeric, 5)
            }, log);
            var mapFields = new[] { A("ConfigId", "Configuration Id", 20), A("SapKey", "SAP Key", 100), A("ErpName", "GST Portal Name", 140), A("Created", "Created By Add-on", 1), A("Note", "Note", 254) };
            EnsureFields("@" + Constants.UdtCustMap, mapFields, log);
            EnsureFields("@" + Constants.UdtItemMap, mapFields, log);
            EnsureFields("@" + Constants.UdtWhsMap, mapFields, log);

            // --- UDFs on A/R Invoice (OINV) ---
            EnsureFields("OINV", new[]
            {
                A(Constants.UdfErpDoc, "GST Portal Invoice", 60), A(Constants.UdfErpStatus, "GST Portal Sync Status", 20),
                A(Constants.UdfLastSync, "Last Sync", 20), A(Constants.UdfSyncError, "Sync Error", 254)
            }, log);
            // --- GST documents (large signed payloads only in memo fields of this UDT; SAP standard tables stay untouched) ---
            EnsureTable(Constants.UdtDocs, "GST Portal GST documents", log);
            EnsureFields("@" + Constants.UdtDocs, new[]
            {
                A("ConfigId", "Configuration Id", 20), new Fld("DocEntry", "SAP DocEntry", BoFieldTypes.db_Numeric, 11), new Fld("DocNum", "SAP DocNum", BoFieldTypes.db_Numeric, 11),
                A("ErpInv", "GST Portal Invoice ID", 60), A("Customer", "Customer", 100), A("Gstin", "Customer GSTIN", 20), A("InvDate", "Invoice Date", 10),
                new Fld("Taxable", "Taxable Amount", BoFieldTypes.db_Float, 0, BoFldSubTypes.st_Sum), new Fld("GstAmt", "GST Amount", BoFieldTypes.db_Float, 0, BoFldSubTypes.st_Sum),
                A("EInvStatus", "IRN Status", 20), A("Irn", "IRN", 64), A("AckNo", "Ack Number", 30), A("AckDate", "Ack Date", 20),
                new Fld("SignedQr", "Signed QR Code", BoFieldTypes.db_Memo, 0), new Fld("SignedInv", "Signed Invoice", BoFieldTypes.db_Memo, 0),
                A("CancelSt", "Cancellation Status", 40), A("EwbStatus", "E-Way Bill Status", 20), A("EwbNo", "E-Way Bill No", 20), A("EwbDate", "E-Way Bill Date", 20), A("EwbValid", "E-Way Bill Valid Until", 20),
                A("Transpr", "Transporter", 100), A("Vehicle", "Vehicle", 20), A("Mode", "Transport Mode", 10), new Fld("Distance", "Distance", BoFieldTypes.db_Float, 0, BoFldSubTypes.st_Quantity),
                A("LastSync", "Last Sync", 20), A("Error", "Error Message", 254)
            }, log);
            EnsureTable(Constants.UdtTxn, "GST integration history", log);
            EnsureFields("@" + Constants.UdtTxn, new[]
            {
                A("ConfigId", "Configuration Id", 20), new Fld("DocEntry", "SAP DocEntry", BoFieldTypes.db_Numeric, 11), A("Operation", "Operation", 30), A("Status", "Status", 40),
                A("RequestId", "Request ID", 40), A("Reference", "Transaction Reference", 120), A("Message", "Message", 254), A("Ts", "Timestamp", 20)
            }, log);
            EnsureFields("OINV", new[]
            {
                A(Constants.UdfGstStatus, "GST Status", 20), A(Constants.UdfGstSyncDate, "GST Sync Date", 20), A(Constants.UdfGstError, "GST Error", 254), A(Constants.UdfErpRequestId, "ERP Request ID", 40)
            }, log);
            EnsureFields("OINV", new[]
            {
                A(Constants.UdfEInvStatus, "IRN Status", 20), A(Constants.UdfIrn, "IRN", 64), A(Constants.UdfAckNo, "Ack Number", 30), A(Constants.UdfAckDate, "Ack Date", 20),
                A(Constants.UdfEwbStatus, "E-Way Bill Status", 20), A(Constants.UdfEwbNo, "E-Way Bill No", 20), A(Constants.UdfEwbDate, "E-Way Bill Date", 20),
                A(Constants.UdfEwbValid, "E-Way Bill Valid Until", 20), A(Constants.UdfCancelStatus, "Cancellation Status", 40),
                new Fld(Constants.UdfSignedQr, "Signed QR Code", BoFieldTypes.db_Memo, 0), A(Constants.UdfQrPath, "QR Image Path", 254)
            }, log);
            return log;
        }

        private void EnsureTable(string name, string desc, List<string> log)
        {
            object exists = _di.Scalar("SELECT COUNT(*) FROM OUTB WHERE TableName='" + name + "'");
            if (Convert.ToInt32(exists) > 0) return;
            var ut = (UserTablesMD)_di.Company.GetBusinessObject(BoObjectTypes.oUserTables);
            try
            {
                ut.TableName = name; ut.TableDescription = desc; ut.TableType = BoUTBTableType.bott_NoObject;
                int rc = ut.Add();
                if (rc != 0) throw new InvalidOperationException("Creating UDT @" + name + " failed: " + _di.LastError());
                log.Add("UDT @" + name + " created");
            }
            finally { Marshal.ReleaseComObject(ut); }
        }

        private void EnsureFields(string table, IEnumerable<Fld> fields, List<string> log)
        {
            foreach (var f in fields)
            {
                object exists = _di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='" + table + "' AND AliasID='" + f.Name + "'");
                if (Convert.ToInt32(exists) > 0) continue;
                var uf = (UserFieldsMD)_di.Company.GetBusinessObject(BoObjectTypes.oUserFields);
                try
                {
                    uf.TableName = table; uf.Name = f.Name; uf.Description = f.Desc; uf.Type = f.Type; uf.SubType = f.Sub;
                    if (f.Size > 0) uf.EditSize = f.Size;
                    int rc = uf.Add();
                    if (rc != 0) throw new InvalidOperationException("Creating field U_" + f.Name + " on " + table + " failed: " + _di.LastError());
                    log.Add("UDF U_" + f.Name + " on " + table + " created");
                }
                finally { Marshal.ReleaseComObject(uf); }
            }
        }

        /// <summary>True when every table and field exists.</summary>
        public bool IsInstalled()
        {
            foreach (var t in new[] { Constants.UdtSync, Constants.UdtCustMap, Constants.UdtItemMap, Constants.UdtWhsMap })
                if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM OUTB WHERE TableName='" + t + "'")) == 0) return false;
            if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM OUTB WHERE TableName='" + Constants.UdtDocs + "'")) == 0) return false;
            if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='@" + Constants.UdtSync + "' AND AliasID='ConfigId'")) == 0) return false;      // v1.1 column
            if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='OINV' AND AliasID='" + Constants.UdfEwbNo + "'")) == 0) return false;
            if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM OUTB WHERE TableName='" + Constants.UdtTxn + "'")) == 0) return false;
            if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='OINV' AND AliasID='" + Constants.UdfGstStatus + "'")) == 0) return false;
            return Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='OINV' AND AliasID IN ('" + Constants.UdfErpDoc + "','" + Constants.UdfErpStatus + "','" + Constants.UdfLastSync + "','" + Constants.UdfSyncError + "')")) == 4;
        }
    }
}
