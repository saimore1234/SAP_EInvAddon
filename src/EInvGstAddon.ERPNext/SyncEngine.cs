using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    /// <summary>One synchronisation attempt of one SAP invoice. Safe to call repeatedly (idempotent).</summary>
    public sealed class SyncEngine
    {
        private readonly ErpClient _erp; private readonly ISapSource _sap; private readonly IStateStore _store; private readonly AppConfig _cfg;
        private readonly InvoiceSync _inv;
        private readonly ErpLookups _lk;

        public SyncEngine(ErpClient erp, ISapSource sap, IStateStore store)
        {
            _erp = erp; _sap = sap; _store = store; _cfg = erp.Config;
            _lk = new ErpLookups(erp);
            var whs = new WarehouseSync(erp, store, sap, _lk);
            var cust = new CustomerSync(erp, store, sap, _lk);
            var items = new ItemSync(erp, store, sap, _lk, whs);
            _inv = new InvoiceSync(erp, sap, store, _lk, cust, items, whs);
        }

        public SyncOutcome Process(int docEntry)
        {
            var outcome = new SyncOutcome();
            var rec = _store.GetRecord(docEntry) ?? new SyncRecord { DocEntry = docEntry, Database = _cfg.SapDatabase, Status = SyncStatus.Pending };
            if (rec.Status == SyncStatus.Synced && !string.IsNullOrEmpty(rec.ErpInvoice))
            {
                outcome.Success = true; outcome.Status = SyncStatus.Synced; outcome.ErpInvoice = rec.ErpInvoice; outcome.Message = "Already synchronised.";
                return outcome;
            }

            SapInvoice inv = null;
            try
            {
                inv = _sap.GetInvoice(docEntry);
                inv.RefSuffix = _cfg.ReferenceSuffix;
                rec.DocNum = inv.DocNum; rec.Database = inv.Database; rec.Status = SyncStatus.Processing; rec.Error = null;
                _store.SaveRecord(rec);

                // 1) idempotency: a Sales Invoice for this SAP document may already exist (previous attempt, lost response, ...)
                var existing = _inv.FindExisting(inv.SyncReference);
                if (existing != null)
                {
                    outcome.Warnings.Add("Existing GST Portal invoice found for " + inv.SyncReference + "; no new invoice created.");
                    return Finish(rec, inv, existing, outcome, true);
                }

                // 2) create customer / items / warehouses as needed and the draft invoice
                JObject created;
                try { created = _inv.CreateDraft(inv, outcome); }
                catch (ErpApiException ex) when (IsDuplicate(ex))
                {
                    created = _inv.FindExisting(inv.SyncReference);
                    if (created == null) throw;
                }
                return Finish(rec, inv, created, outcome, false);
            }
            catch (ErpUnavailableException ex)
            {
                // Uncertain create result is resolved by FindExisting on the next attempt.
                return Fail(rec, outcome, SyncStatus.Pending, "GST Portal unavailable: " + ex.Message, true);
            }
            catch (ErpAuthException ex) { return Fail(rec, outcome, SyncStatus.Failed, "Invalid GST Portal credentials/permissions: " + ex.Message, false); }
            catch (ConfigurationException ex) { return Fail(rec, outcome, SyncStatus.Failed, ex.Message, false); }
            catch (ErpApiException ex) { return Fail(rec, outcome, SyncStatus.Failed, "GST Portal rejected the request: " + ex.Message, false); }
            catch (Exception ex)
            {
                Logger.Error("sync.unexpected", ex, "docEntry", docEntry);
                return Fail(rec, outcome, SyncStatus.Failed, "Unexpected error: " + ex.Message, false);
            }
        }

        private static bool IsDuplicate(ErpApiException ex)
        {
            return string.Equals(ex.ExcType, "DuplicateEntryError", StringComparison.OrdinalIgnoreCase) || (ex.Message ?? "").IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private SyncOutcome Finish(SyncRecord rec, SapInvoice inv, JObject erp, SyncOutcome outcome, bool wasExisting)
        {
            string company = (string)erp["company"];
            if (company != null && !string.Equals(company, _cfg.ErpCompany, StringComparison.OrdinalIgnoreCase))
                return Fail(rec, outcome, SyncStatus.Failed, "Safety stop: GST Portal invoice belongs to company '" + company + "'.", false);

            // creation response of the REST API holds full data; for an existing one we have the summary fields
            outcome.ErpInvoice = (string)erp["name"];
            outcome.ErpDocStatus = ((int)(erp["docstatus"] ?? 0)) == 0 ? "Draft" : ((int)erp["docstatus"] == 1 ? "Submitted" : "Cancelled");
            outcome.ErpGrandTotal = erp["rounded_total"] != null && (decimal)erp["rounded_total"] != 0 ? (decimal)erp["rounded_total"] : (decimal)(erp["grand_total"] ?? 0);
            foreach (var d in InvoiceSync.Compare(inv, erp)) outcome.Warnings.Add(d);

            rec.ErpInvoice = outcome.ErpInvoice; rec.CustomerRef = (string)erp["customer"]; rec.Status = SyncStatus.Synced;
            rec.SyncDate = DateTime.Now;
            rec.Error = outcome.Warnings.Count == 0 ? "" : Util.Trunc(string.Join(" | ", outcome.Warnings.Where(w => !w.StartsWith("Company ")).ToArray()), 250);
            _store.SaveRecord(rec);
            outcome.Success = true; outcome.Status = SyncStatus.Synced;
            outcome.Message = (wasExisting ? "Already in GST Portal as " : "Draft Sales Invoice created: ") + outcome.ErpInvoice;
            Logger.Info("sync.done", "docEntry", inv.DocEntry, "erp", outcome.ErpInvoice, "existing", wasExisting, "total", outcome.ErpGrandTotal);
            return outcome;
        }

        private SyncOutcome Fail(SyncRecord rec, SyncOutcome outcome, string status, string message, bool retryable)
        {
            rec.RetryCount++;
            rec.SyncDate = DateTime.Now;     // last attempt; the recovery timer derives the back-off from it
            bool giveUp = retryable && rec.RetryCount > _cfg.MaxAutoRetries;
            rec.Status = giveUp ? SyncStatus.RetryRequired : status;
            rec.Error = Util.Trunc(message, 250);
            try { _store.SaveRecord(rec); } catch (Exception ex) { Logger.Error("sync.save.failed", ex); }
            outcome.Success = false; outcome.Status = rec.Status; outcome.Message = message;
            Logger.Warn("sync.failed", "docEntry", rec.DocEntry, "status", rec.Status, "error", message);
            return outcome;
        }
    }

    /// <summary>Background worker: processes queued invoices one at a time, off the SAP UI thread.</summary>
    public sealed class SyncQueue : IDisposable
    {
        private readonly BlockingCollection<int> _q = new BlockingCollection<int>();
        private readonly HashSet<int> _queued = new HashSet<int>();
        private readonly Func<SyncEngine> _engineFactory;
        private readonly Thread _worker;
        private readonly Action<int, SyncOutcome> _onResult;
        private readonly Dictionary<int, DateTime> _notBefore = new Dictionary<int, DateTime>();

        public int Backlog { get { lock (_queued) return _queued.Count; } }

        public SyncQueue(Func<SyncEngine> engineFactory, Action<int, SyncOutcome> onResult)
        {
            _engineFactory = engineFactory; _onResult = onResult;
            _worker = new Thread(Run) { IsBackground = true, Name = "EInvGst-SyncQueue" };
            _worker.Start();
        }

        public bool Enqueue(int docEntry)
        {
            lock (_queued) { if (!_queued.Add(docEntry)) return false; }
            _q.Add(docEntry);
            return true;
        }

        private void Run()
        {
            foreach (var docEntry in _q.GetConsumingEnumerable())
            {
                SyncOutcome outcome;
                try { outcome = _engineFactory().Process(docEntry); }
                catch (Exception ex) { Logger.Error("queue.item.failed", ex, "docEntry", docEntry); outcome = new SyncOutcome { Status = SyncStatus.Failed, Message = ex.Message }; }
                lock (_queued) _queued.Remove(docEntry);
                try { _onResult(docEntry, outcome); } catch (Exception ex) { Logger.Error("queue.callback.failed", ex); }
            }
        }

        public void Dispose() { _q.CompleteAdding(); }
    }
}
