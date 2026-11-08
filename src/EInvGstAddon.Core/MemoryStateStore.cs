using System.Collections.Generic;
using System.Linq;

namespace EInvGstAddon.Core
{
    /// <summary>In-memory IStateStore used by tests and dry runs. Thread-safe.</summary>
    public sealed class MemoryStateStore : IStateStore
    {
        private readonly object _g = new object();
        private readonly Dictionary<int, SyncRecord> _recs = new Dictionary<int, SyncRecord>();
        private readonly Dictionary<string, MapEntry> _c = new Dictionary<string, MapEntry>(), _i = new Dictionary<string, MapEntry>(), _w = new Dictionary<string, MapEntry>();

        private static SyncRecord Clone(SyncRecord r)
        {
            return new SyncRecord { DocEntry = r.DocEntry, DocNum = r.DocNum, Database = r.Database, ErpInvoice = r.ErpInvoice, CustomerRef = r.CustomerRef, Status = r.Status, Error = r.Error, SyncDate = r.SyncDate, RetryCount = r.RetryCount, Code = r.Code, NextAttempt = r.NextAttempt };
        }

        public SyncRecord GetRecord(int docEntry) { lock (_g) { SyncRecord r; return _recs.TryGetValue(docEntry, out r) ? Clone(r) : null; } }
        public void SaveRecord(SyncRecord rec) { lock (_g) _recs[rec.DocEntry] = Clone(rec); }
        public List<SyncRecord> ListRecords(string status, int max) { lock (_g) return _recs.Values.Where(r => status == null || r.Status == status).Take(max).Select(Clone).ToList(); }
        public Dictionary<string, int> CountByStatus() { lock (_g) return _recs.Values.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()); }

        private static MapEntry Get(Dictionary<string, MapEntry> d, string k, object g) { lock (g) { MapEntry e; return d.TryGetValue(k, out e) ? e : null; } }
        public MapEntry GetCustomerMap(string k) { return Get(_c, k, _g); }
        public void SaveCustomerMap(MapEntry e) { lock (_g) _c[e.SapKey] = e; }
        public MapEntry GetItemMap(string k) { return Get(_i, k, _g); }
        public void SaveItemMap(MapEntry e) { lock (_g) _i[e.SapKey] = e; }
        public MapEntry GetWarehouseMap(string k) { return Get(_w, k, _g); }
        public void SaveWarehouseMap(MapEntry e) { lock (_g) _w[e.SapKey] = e; }
        public void RemoveCustomerMap(string k) { lock (_g) _c.Remove(k); }
        public void RemoveItemMap(string k) { lock (_g) _i.Remove(k); }
        public void RemoveWarehouseMap(string k) { lock (_g) _w.Remove(k); }

        public int CustomerMaps { get { lock (_g) return _c.Count; } }
        public int ItemMaps { get { lock (_g) return _i.Count; } }
    }
}

namespace EInvGstAddon.Core
{
    /// <summary>In-memory IGstStore for tests.</summary>
    public sealed class MemoryGstStore : IGstStore
    {
        private readonly System.Collections.Generic.Dictionary<int, GstRecord> _d = new System.Collections.Generic.Dictionary<int, GstRecord>();
        public GstRecord GetGst(int docEntry) { lock (_d) { GstRecord r; return _d.TryGetValue(docEntry, out r) ? r : null; } }
        public void SaveGst(GstRecord rec) { lock (_d) _d[rec.DocEntry] = rec; }
        private readonly System.Collections.Generic.List<GstHistory> _h = new System.Collections.Generic.List<GstHistory>();
        public void AddHistory(GstHistory h) { lock (_h) _h.Add(h); }
        public System.Collections.Generic.List<GstHistory> ListHistory(int docEntry, int max)
        { lock (_h) return System.Linq.Enumerable.ToList(System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(System.Linq.Enumerable.Where(_h, x => x.DocEntry == docEntry), x => x.At), max)); }
    }
}
