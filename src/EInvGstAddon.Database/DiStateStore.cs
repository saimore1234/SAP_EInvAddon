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
    /// (OINV/INV1 are never touched with SQL writes). Safe to call from worker threads: DI work runs on the UI thread.
    /// </summary>
    public sealed class DiStateStore : IStateStore
    {
        private readonly DiSession _di;
        private static string Lit(string s) { return SapReader.Lit(s); }
        public DiStateStore(DiSession di) { _di = di; }

        private T Ui<T>(Func<T> f) { return _di.Ui.Invoke(f); }

        private static string MapCode(string key)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""));
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
                var rows = _di.Query("SELECT " + SyncCols + " FROM [@" + Constants.UdtSync + "] WHERE Code=" + Lit(docEntry.ToString(CultureInfo.InvariantCulture)));
                return rows.Count == 0 ? null : ToRec(rows[0]);
            });
        }

        public void SaveRecord(SyncRecord rec)
        {
            Ui<object>(() =>
            {
                string code = rec.DocEntry.ToString(CultureInfo.InvariantCulture);
                var t = _di.Company.UserTables.Item(Constants.UdtSync);
                try
                {
                    bool exists = t.GetByKey(code);
                    if (!exists) { t.Code = code; t.Name = code; }
                    var f = t.UserFields.Fields;
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
                string where = string.IsNullOrEmpty(status) ? "" : " WHERE U_Status=" + Lit(status);
                var l = new List<SyncRecord>();
                foreach (var r in _di.Query("SELECT TOP " + max + " " + SyncCols + " FROM [@" + Constants.UdtSync + "]" + where + " ORDER BY CAST(Code AS INT) DESC")) l.Add(ToRec(r));
                return l;
            });
        }

        public Dictionary<string, int> CountByStatus()
        {
            return Ui(() =>
            {
                var d = new Dictionary<string, int>();
                foreach (var r in _di.Query("SELECT U_Status AS S, COUNT(*) AS C FROM [@" + Constants.UdtSync + "] GROUP BY U_Status")) d[S(r, "S")] = I(r, "C");
                return d;
            });
        }

        // ---------------- mappings ----------------
        private MapEntry GetMap(string udt, string key)
        {
            return Ui(() =>
            {
                var rows = _di.Query("SELECT U_SapKey,U_ErpName,U_Created,U_Note FROM [@" + udt + "] WHERE Code=" + Lit(MapCode(key)));
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
