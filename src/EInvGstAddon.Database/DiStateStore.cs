using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using SAPbobsCOM;

namespace EInvGstAddon.Database
{
    /// <summary>
    /// Persists synchronisation state in the add-on's UDTs and the invoice UDFs, always through the DI API
    /// (OINV/INV1 are never touched with SQL writes). Every row carries U_ConfigId so two integration configurations
    /// (different ERPNext servers / companies) can never see or overwrite each other's mappings or queue entries.
    /// Safe to call from worker threads: DI work runs on the UI thread.
    /// </summary>
    public sealed class DiStateStore : IStateStore, IGstStore
    {
        private readonly DiSession _di;
        private readonly string _cfgId;
        private static string Lit(string s) { return SapReader.Lit(s); }

        /// <param name="configId">AppConfig.StoredConfigId ("" for the migrated Phase 1 configuration).</param>
        public DiStateStore(DiSession di, string configId) { _di = di; _cfgId = configId ?? ""; }

        public string ConfigId { get { return _cfgId; } }
        private string CfgWhere { get { return "ISNULL(U_ConfigId,'')=" + Lit(_cfgId); } }
        private string RowCode(int docEntry) { var d = docEntry.ToString(CultureInfo.InvariantCulture); return _cfgId.Length == 0 ? d : _cfgId + "-" + d; }

        private T Ui<T>(Func<T> f) { return _di.Ui.Invoke(f); }

        private string MapCode(string key)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(_cfgId.Length == 0 ? (key ?? "") : _cfgId + "|" + (key ?? "")));
                var sb = new StringBuilder();
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, 24);
            }
        }

        private static string S(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : ""; }
        private static int I(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }

        // ---------------- sync records ----------------
        private const string SyncCols = "Code,U_DocEntry,U_DocNum,U_Database,U_ErpInv,U_Customer,U_Status,U_SyncTs,U_Error,U_Retry";

        private static SyncRecord ToRec(Dictionary<string, object> r)
        {
            DateTime ts;
            return new SyncRecord
            {
                Code = S(r, "Code"), DocEntry = I(r, "U_DocEntry"), DocNum = I(r, "U_DocNum"), Database = S(r, "U_Database"), ErpInvoice = S(r, "U_ErpInv"),
                CustomerRef = S(r, "U_Customer"), Status = S(r, "U_Status"), Error = S(r, "U_Error"), RetryCount = I(r, "U_Retry"),
                SyncDate = DateTime.TryParseExact(S(r, "U_SyncTs"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out ts) ? (DateTime?)ts : null
            };
        }

        public SyncRecord GetRecord(int docEntry)
        {
            return Ui(() =>
            {
                var rows = _di.Query("SELECT " + SyncCols + " FROM [@" + Constants.UdtSync + "] WHERE Code=" + Lit(RowCode(docEntry)) + " AND " + CfgWhere);
                return rows.Count == 0 ? null : ToRec(rows[0]);
            });
        }

        public void SaveRecord(SyncRecord rec)
        {
            Ui<object>(() =>
            {
                string code = RowCode(rec.DocEntry);
                var t = _di.Company.UserTables.Item(Constants.UdtSync);
                try
                {
                    bool exists = t.GetByKey(code);
                    if (!exists) { t.Code = code; t.Name = code; }
                    var f = t.UserFields.Fields;
                    f.Item("U_ConfigId").Value = _cfgId;
                    f.Item("U_DocEntry").Value = rec.DocEntry;
                    f.Item("U_DocNum").Value = rec.DocNum;
                    f.Item("U_Database").Value = Trunc(rec.Database, 30);
                    f.Item("U_ErpInv").Value = Trunc(rec.ErpInvoice, 60);
                    f.Item("U_Customer").Value = Trunc(rec.CustomerRef, 140);
                    f.Item("U_Status").Value = Trunc(rec.Status, 20);
                    f.Item("U_SyncTs").Value = rec.SyncDate.HasValue ? rec.SyncDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
                    f.Item("U_Error").Value = Trunc(rec.Error, 254);
                    f.Item("U_Retry").Value = rec.RetryCount;
                    int rc = exists ? t.Update() : t.Add();
                    if (rc != 0) throw new InvalidOperationException("Saving @" + Constants.UdtSync + " row " + code + " failed: " + _di.LastError());
                }
                finally { Marshal.ReleaseComObject(t); }

                if (rec.Status != SyncStatus.Processing) UpdateInvoiceUdf(rec);
                return null;
            });
        }

        /// <summary>Writes the status UDFs of the A/R invoice with the DI API (never SQL).</summary>
        private void UpdateInvoiceUdf(SyncRecord rec)
        {
            var d = (Documents)_di.Company.GetBusinessObject(BoObjectTypes.oInvoices);
            try
            {
                if (!d.GetByKey(rec.DocEntry)) { Logger.Warn("udf.invoice.notfound", "docEntry", rec.DocEntry); return; }
                var f = d.UserFields.Fields;
                string newDoc = Trunc(rec.ErpInvoice, 60), newStatus = Trunc(rec.Status, 20), newErr = Trunc(rec.Error, 254);
                string ts = (rec.SyncDate ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (Convert.ToString(f.Item("U_" + Constants.UdfErpDoc).Value) == newDoc && Convert.ToString(f.Item("U_" + Constants.UdfErpStatus).Value) == newStatus
                    && Convert.ToString(f.Item("U_" + Constants.UdfSyncError).Value) == newErr) return;      // nothing changed
                f.Item("U_" + Constants.UdfErpDoc).Value = newDoc;
                f.Item("U_" + Constants.UdfErpStatus).Value = newStatus;
                f.Item("U_" + Constants.UdfLastSync).Value = ts;
                f.Item("U_" + Constants.UdfSyncError).Value = newErr;
                int rc = d.Update();
                if (rc != 0) Logger.Warn("udf.invoice.update.failed", "docEntry", rec.DocEntry, "error", _di.LastError());
            }
            finally { Marshal.ReleaseComObject(d); }
        }

        private static string Trunc(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n); }

        public List<SyncRecord> ListRecords(string status, int max)
        {
            return Ui(() =>
            {
                string where = " WHERE " + CfgWhere + (string.IsNullOrEmpty(status) ? "" : " AND U_Status=" + Lit(status));
                var l = new List<SyncRecord>();
                foreach (var r in _di.Query("SELECT TOP " + max + " " + SyncCols + " FROM [@" + Constants.UdtSync + "]" + where + " ORDER BY U_DocEntry DESC")) l.Add(ToRec(r));
                return l;
            });
        }

        public Dictionary<string, int> CountByStatus()
        {
            return Ui(() =>
            {
                var d = new Dictionary<string, int>();
                foreach (var r in _di.Query("SELECT U_Status AS S, COUNT(*) AS C FROM [@" + Constants.UdtSync + "] WHERE " + CfgWhere + " GROUP BY U_Status")) d[S(r, "S")] = I(r, "C");
                return d;
            });
        }

        // ---------------- mappings ----------------
        private MapEntry GetMap(string udt, string key)
        {
            return Ui(() =>
            {
                var rows = _di.Query("SELECT U_SapKey,U_ErpName,U_Created,U_Note FROM [@" + udt + "] WHERE Code=" + Lit(MapCode(key)) + " AND " + CfgWhere);
                if (rows.Count == 0) return null;
                return new MapEntry { SapKey = S(rows[0], "U_SapKey"), ErpName = S(rows[0], "U_ErpName"), Created = S(rows[0], "U_Created"), Note = S(rows[0], "U_Note") };
            });
        }

        private void SaveMap(string udt, MapEntry e)
        {
            Ui<object>(() =>
            {
                string code = MapCode(e.SapKey);
                var t = _di.Company.UserTables.Item(udt);
                try
                {
                    bool exists = t.GetByKey(code);
                    if (!exists) { t.Code = code; t.Name = code; }
                    t.UserFields.Fields.Item("U_ConfigId").Value = _cfgId;
                    t.UserFields.Fields.Item("U_SapKey").Value = Trunc(e.SapKey, 100);
                    t.UserFields.Fields.Item("U_ErpName").Value = Trunc(e.ErpName, 140);
                    t.UserFields.Fields.Item("U_Created").Value = Trunc(e.Created, 1);
                    t.UserFields.Fields.Item("U_Note").Value = Trunc(e.Note, 254);
                    int rc = exists ? t.Update() : t.Add();
                    if (rc != 0) throw new InvalidOperationException("Saving mapping in @" + udt + " failed: " + _di.LastError());
                }
                finally { Marshal.ReleaseComObject(t); }
                return null;
            });
        }

        private void RemoveMap(string udt, string key)
        {
            Ui<object>(() =>
            {
                var t = _di.Company.UserTables.Item(udt);
                try { if (t.GetByKey(MapCode(key))) t.Remove(); }       // add-on's own mapping row, not an SAP business document
                finally { Marshal.ReleaseComObject(t); }
                return null;
            });
        }


        // ---------------- GST documents (@EINV_DOCS + invoice UDFs) ----------------
        private const string DocCols = "U_DocEntry,U_DocNum,U_ErpInv,U_Customer,U_Gstin,U_InvDate,U_Taxable,U_GstAmt,U_EInvStatus,U_Irn,U_AckNo,U_AckDate,U_SignedQr,U_SignedInv,U_CancelSt,U_EwbStatus,U_EwbNo,U_EwbDate,U_EwbValid,U_Transpr,U_Vehicle,U_Mode,U_Distance,U_LastSync,U_Error";

        public GstRecord GetGst(int docEntry)
        {
            return Ui(() =>
            {
                var rows = _di.Query("SELECT " + DocCols + " FROM [@" + Constants.UdtDocs + "] WHERE Code=" + Lit(RowCode(docEntry)) + " AND " + CfgWhere);
                if (rows.Count == 0) return null;
                var r = rows[0];
                DateTime d;
                return new GstRecord
                {
                    DocEntry = I(r, "U_DocEntry"), DocNum = I(r, "U_DocNum"), ErpInvoice = S(r, "U_ErpInv"), Customer = S(r, "U_Customer"), Gstin = S(r, "U_Gstin"),
                    InvoiceDate = DateTime.TryParse(S(r, "U_InvDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null,
                    Taxable = Dec(r, "U_Taxable"), Gst = Dec(r, "U_GstAmt"), EInvStatus = S(r, "U_EInvStatus"), Irn = S(r, "U_Irn"), AckNo = S(r, "U_AckNo"), AckDate = S(r, "U_AckDate"),
                    SignedQr = S(r, "U_SignedQr"), SignedInvoice = S(r, "U_SignedInv"), CancelStatus = S(r, "U_CancelSt"), EwbStatus = S(r, "U_EwbStatus"), EwbNo = S(r, "U_EwbNo"),
                    EwbDate = S(r, "U_EwbDate"), EwbValid = S(r, "U_EwbValid"), Transporter = S(r, "U_Transpr"), Vehicle = S(r, "U_Vehicle"), Mode = S(r, "U_Mode"), Distance = Dec(r, "U_Distance"),
                    Error = S(r, "U_Error"), LastSync = DateTime.TryParse(S(r, "U_LastSync"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null
                };
            });
        }

        private static decimal Dec(Dictionary<string, object> r, string k) { object v; return r.TryGetValue(k, out v) && v != null ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : 0m; }

        public void SaveGst(GstRecord g)
        {
            Ui<object>(() =>
            {
                string code = RowCode(g.DocEntry);
                var t = _di.Company.UserTables.Item(Constants.UdtDocs);
                try
                {
                    bool exists = t.GetByKey(code);
                    if (!exists) { t.Code = code; t.Name = code; }
                    var f = t.UserFields.Fields;
                    f.Item("U_ConfigId").Value = _cfgId; f.Item("U_DocEntry").Value = g.DocEntry; f.Item("U_DocNum").Value = g.DocNum;
                    f.Item("U_ErpInv").Value = Trunc(g.ErpInvoice, 60); f.Item("U_Customer").Value = Trunc(g.Customer, 100); f.Item("U_Gstin").Value = Trunc(g.Gstin, 20);
                    f.Item("U_InvDate").Value = g.InvoiceDate.HasValue ? g.InvoiceDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
                    f.Item("U_Taxable").Value = (double)g.Taxable; f.Item("U_GstAmt").Value = (double)g.Gst;
                    f.Item("U_EInvStatus").Value = Trunc(g.EInvStatus, 20); f.Item("U_Irn").Value = Trunc(g.Irn, 64); f.Item("U_AckNo").Value = Trunc(g.AckNo, 30); f.Item("U_AckDate").Value = Trunc(g.AckDate, 20);
                    f.Item("U_SignedQr").Value = g.SignedQr ?? ""; f.Item("U_SignedInv").Value = g.SignedInvoice ?? "";
                    f.Item("U_CancelSt").Value = Trunc(g.CancelStatus, 40); f.Item("U_EwbStatus").Value = Trunc(g.EwbStatus, 20); f.Item("U_EwbNo").Value = Trunc(g.EwbNo, 20);
                    f.Item("U_EwbDate").Value = Trunc(g.EwbDate, 20); f.Item("U_EwbValid").Value = Trunc(g.EwbValid, 20);
                    f.Item("U_Transpr").Value = Trunc(g.Transporter, 100); f.Item("U_Vehicle").Value = Trunc(g.Vehicle, 20); f.Item("U_Mode").Value = Trunc(g.Mode, 10); f.Item("U_Distance").Value = (double)g.Distance;
                    f.Item("U_LastSync").Value = (g.LastSync ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); f.Item("U_Error").Value = Trunc(g.Error, 254);
                    int rc = exists ? t.Update() : t.Add();
                    if (rc != 0) throw new InvalidOperationException("Saving @" + Constants.UdtDocs + " failed: " + _di.LastError());
                }
                finally { Marshal.ReleaseComObject(t); }
                UpdateInvoiceGstUdf(g);
                return null;
            });
        }

        /// <summary>Small status values only (the signed payloads stay in the UDT memo fields). DI API, never SQL.</summary>
        private void UpdateInvoiceGstUdf(GstRecord g)
        {
            var d = (Documents)_di.Company.GetBusinessObject(BoObjectTypes.oInvoices);
            try
            {
                if (!d.GetByKey(g.DocEntry)) return;
                var f = d.UserFields.Fields;
                f.Item("U_" + Constants.UdfEInvStatus).Value = Trunc(g.EInvStatus, 20);
                // U_IRN / U_AckNo etc. can be shared with a previous e-invoice add-on: a status read that knows nothing about the document
                // must never blank them. Only real values are written.
                Keep(f, Constants.UdfIrn, g.Irn, 64); Keep(f, Constants.UdfAckNo, g.AckNo, 30); Keep(f, Constants.UdfAckDate, g.AckDate, 20);
                f.Item("U_" + Constants.UdfEwbStatus).Value = Trunc(g.EwbStatus, 20);
                Keep(f, Constants.UdfEwbNo, g.EwbNo, 20); Keep(f, Constants.UdfEwbDate, g.EwbDate, 20); Keep(f, Constants.UdfEwbValid, g.EwbValid, 20);
                Keep(f, Constants.UdfCancelStatus, g.CancelStatus, 40);
                string overall = (g.EInvStatus == "Unknown" || g.EwbStatus == "Unknown") ? "Unknown" : (g.EInvStatus == "Generated" || g.EwbStatus == "Generated") ? "Generated" : (g.EInvStatus == "Cancelled" ? "Cancelled" : "Not generated");
                f.Item("U_" + Constants.UdfGstStatus).Value = overall;
                f.Item("U_" + Constants.UdfGstSyncDate).Value = (g.LastSync ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                f.Item("U_" + Constants.UdfGstError).Value = Trunc(g.Error, 254);
                int rc = d.Update();
                if (rc != 0) Logger.Warn("udf.gst.update.failed", "docEntry", g.DocEntry, "error", _di.LastError());
            }
            finally { Marshal.ReleaseComObject(d); }
        }
        private static void Keep(Fields f, string name, string value, int max)
        {
            if (!string.IsNullOrEmpty(value)) f.Item("U_" + name).Value = Trunc(value, max);
        }
        private static void SetOptional(Fields f, string name, string value)
        {
            try { f.Item("U_" + name).Value = value ?? ""; }
            catch (Exception ex) { Logger.Warn("udf.optional.missing", "field", "U_" + name, "error", ex.Message); }
        }
        public void AddHistory(GstHistory h)
        {
            Ui<object>(() =>
            {
                string code = Guid.NewGuid().ToString("N").Substring(0, 24);
                var t = _di.Company.UserTables.Item(Constants.UdtTxn);
                try
                {
                    t.Code = code; t.Name = code;
                    var f = t.UserFields.Fields;
                    f.Item("U_ConfigId").Value = _cfgId; f.Item("U_DocEntry").Value = h.DocEntry; f.Item("U_Operation").Value = Trunc(h.Operation, 30); f.Item("U_Status").Value = Trunc(h.Status, 40);
                    f.Item("U_RequestId").Value = Trunc(h.RequestId, 40); f.Item("U_Reference").Value = Trunc(h.Reference, 120); f.Item("U_Message").Value = Trunc(h.Message, 254);
                    f.Item("U_Ts").Value = h.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    int rc = t.Add();
                    if (rc != 0) Logger.Warn("history.add.failed", "error", _di.LastError());
                }
                finally { Marshal.ReleaseComObject(t); }
                return null;
            });
        }

        public List<GstHistory> ListHistory(int docEntry, int max)
        {
            return Ui(() =>
            {
                var l = new List<GstHistory>();
                DateTime ts;
                foreach (var r in _di.Query("SELECT TOP " + max + " U_DocEntry,U_Operation,U_Status,U_RequestId,U_Reference,U_Message,U_Ts FROM [@" + Constants.UdtTxn + "] WHERE " + CfgWhere + " AND U_DocEntry=" + docEntry + " ORDER BY U_Ts DESC"))
                    l.Add(new GstHistory { DocEntry = I(r, "U_DocEntry"), Operation = S(r, "U_Operation"), Status = S(r, "U_Status"), RequestId = S(r, "U_RequestId"), Reference = S(r, "U_Reference"), Message = S(r, "U_Message"),
                        At = DateTime.TryParse(S(r, "U_Ts"), CultureInfo.InvariantCulture, DateTimeStyles.None, out ts) ? ts : DateTime.MinValue });
                return l;
            });
        }

        public MapEntry GetCustomerMap(string k) { return GetMap(Constants.UdtCustMap, k); }
        public void SaveCustomerMap(MapEntry e) { SaveMap(Constants.UdtCustMap, e); }
        public void RemoveCustomerMap(string k) { RemoveMap(Constants.UdtCustMap, k); }
        public MapEntry GetItemMap(string k) { return GetMap(Constants.UdtItemMap, k); }
        public void SaveItemMap(MapEntry e) { SaveMap(Constants.UdtItemMap, e); }
        public void RemoveItemMap(string k) { RemoveMap(Constants.UdtItemMap, k); }
        public MapEntry GetWarehouseMap(string k) { return GetMap(Constants.UdtWhsMap, k); }
        public void SaveWarehouseMap(MapEntry e) { SaveMap(Constants.UdtWhsMap, e); }
        public void RemoveWarehouseMap(string k) { RemoveMap(Constants.UdtWhsMap, k); }
    }
}
