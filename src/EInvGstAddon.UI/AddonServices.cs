using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.Database;
using EInvGstAddon.ERPNext;

namespace EInvGstAddon.UI
{
    /// <summary>Everything the screens and event handlers share. Created once at start-up on the main thread.</summary>
    public sealed class AddonServices
    {
        public SapConnection Sap;
        public AppConfig Cfg;
        public DiSession Di;
        public DiStateStore Store;
        public SapReader Source;
        public SyncQueue Queue;
        public FormRouter Router;
        public Coordinator Coordinator;
        public bool SchemaReady;
        private SyncEngine _engine;
        private readonly object _gate = new object();

        public SAPbouiCOM.Application App { get { return Sap.App; } }

        public ErpClient NewClient() { return new ErpClient(Cfg); }

        /// <summary>Engine used by the (single) worker thread; rebuilt after settings change.</summary>
        public SyncEngine Engine()
        {
            lock (_gate) { if (_engine == null) _engine = new SyncEngine(NewClient(), Source, Store); return _engine; }
        }

        public void ResetEngine() { lock (_gate) _engine = null; }

        public void Alert(string text) { try { App.MessageBox(text, 1, "OK", "", ""); } catch (Exception ex) { Logger.Warn("alert.failed", "error", ex.Message); } }

        public void Status(string text, bool error) { Sap.Status(text, error); }

        /// <summary>Runs slow (network) work off the UI thread and delivers the result back on it.</summary>
        public void Background<T>(string what, Func<T> work, Action<T> done, Action<Exception> failed)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                T result = default(T); Exception err = null;
                try { result = work(); } catch (Exception ex) { err = ex; Logger.Warn("background.failed", "what", what, "error", ex.Message); }
                UiThread.Post(() => { if (err != null) { if (failed != null) failed(err); } else done(result); }, what);
            });
        }
    }

    /// <summary>Invoice event handling, recovery and result display. UI-thread methods only unless stated.</summary>
    public sealed class Coordinator
    {
        private readonly AddonServices _s;
        public Coordinator(AddonServices s) { _s = s; }

        /// <summary>A/R invoice was successfully committed in SAP (FormDataEvent ADD, after action, success).</summary>
        public void OnInvoiceAdded(int docEntry)
        {
            if (!string.Equals(_s.Sap.DatabaseName, _s.Cfg.SapDatabase, StringComparison.OrdinalIgnoreCase)) return;   // company isolation
            UiThread.Post(() =>
            {
                try
                {
                    var rec = _s.Store.GetRecord(docEntry);
                    if (rec != null && rec.Status == SyncStatus.Synced) return;
                    if (rec == null) rec = new SyncRecord { DocEntry = docEntry, Database = _s.Cfg.SapDatabase };
                    rec.Status = SyncStatus.Pending; rec.Error = "";
                    _s.Store.SaveRecord(rec);            // persisted first: a pending invoice is never lost
                    if (_s.Cfg.AutoSync) _s.Queue.Enqueue(docEntry);
                    _s.Status("ERPNext: invoice queued for synchronisation (DocEntry " + docEntry + ").", false);
                }
                catch (Exception ex) { Logger.Error("invoice.added.failed", ex, "docEntry", docEntry); _s.Status("ERPNext sync could not queue DocEntry " + docEntry + ": " + ex.Message, true); }
            }, "invoice-added");
        }

        /// <summary>Manual retry from the Pending / Failed screens. Always re-checks ERPNext first (engine is idempotent).</summary>
        public void Retry(int docEntry)
        {
            var rec = _s.Store.GetRecord(docEntry) ?? new SyncRecord { DocEntry = docEntry, Database = _s.Cfg.SapDatabase };
            if (rec.Status == SyncStatus.Synced) return;
            rec.Status = SyncStatus.Pending; rec.Error = ""; rec.RetryCount = 0;
            _s.Store.SaveRecord(rec);
            _s.Queue.Enqueue(docEntry);
        }

        /// <summary>Runs on the UI thread every 30 s: re-queues Pending invoices whose back-off has elapsed.</summary>
        public void Tick()
        {
            if (!_s.Cfg.AutoSync || !_s.SchemaReady) return;
            try
            {
                foreach (var r in _s.Store.ListRecords(SyncStatus.Pending, 200))
                {
                    double wait = r.RetryCount == 0 ? 0 : Math.Min(900, 30 * Math.Pow(2, r.RetryCount - 1));
                    if (r.SyncDate.HasValue && (DateTime.Now - r.SyncDate.Value).TotalSeconds < wait) continue;
                    _s.Queue.Enqueue(r.DocEntry);
                }
            }
            catch (Exception ex) { Logger.Warn("tick.failed", "error", ex.Message); }
        }

        /// <summary>At start-up: invoices added while the add-on was not running (from SyncStartDocEntry on) become Pending.</summary>
        public void StartupScan()
        {
            try
            {
                if (_s.Cfg.SyncStartDocEntry <= 0)
                {
                    _s.Cfg.SyncStartDocEntry = _s.Source.GetMaxDocEntry() + 1;       // historic invoices are never auto-synchronised
                    ConfigStore.Save(_s.Cfg);
                    Logger.Info("startscan.baseline", "from", _s.Cfg.SyncStartDocEntry);
                }
                foreach (var docEntry in _s.Source.ListUnrecordedInvoices(_s.Cfg.SyncStartDocEntry))
                {
                    var rec = new SyncRecord { DocEntry = docEntry, Database = _s.Cfg.SapDatabase, Status = SyncStatus.Pending };
                    _s.Store.SaveRecord(rec);
                    Logger.Info("startscan.found", "docEntry", docEntry);
                }
            }
            catch (Exception ex) { Logger.Warn("startscan.failed", "error", ex.Message); }
        }

        /// <summary>Queue callback (worker thread): show the result in SAP on the UI thread.</summary>
        public void OnResult(int docEntry, SyncOutcome o)
        {
            UiThread.Post(() =>
            {
                string msg = o.Success ? "ERPNext sync OK: SAP DocEntry " + docEntry + " -> " + o.ErpInvoice + " (" + o.ErpDocStatus + ")"
                                       : "ERPNext sync " + o.Status + " for DocEntry " + docEntry + ": " + o.Message;
                _s.Status(msg, !o.Success);
            }, "sync-result");
        }
    }
}
