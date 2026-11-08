using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;

namespace EInvGstAddon.UI
{
    /// <summary>SAP UI API implementation of <see cref="IMenuHost"/> (the logic that decides what to add lives in Core and is unit-tested).</summary>
    public sealed class SapMenuHost : IMenuHost
    {
        private readonly SAPbouiCOM.Application _app;
        public SapMenuHost(SAPbouiCOM.Application app) { _app = app; }

        public bool Exists(string id) { return _app.Menus.Exists(id); }
        public void AddPopup(string parentId, string id, string caption) { Add(parentId, id, caption, true, true); }
        public void AddItem(string parentId, string id, string caption, bool enabled) { Add(parentId, id, caption, enabled, false); }
        public void Remove(string id) { _app.Menus.RemoveEx(id); }

        private void Add(string parentId, string id, string caption, bool enabled, bool popup)
        {
            SAPbouiCOM.MenuItem parent = null; SAPbouiCOM.Menus subs = null; SAPbouiCOM.MenuCreationParams cp = null;
            try
            {
                parent = _app.Menus.Item(parentId);
                subs = parent.SubMenus;
                cp = (SAPbouiCOM.MenuCreationParams)_app.CreateObject(SAPbouiCOM.BoCreatableObjectType.cot_MenuCreationParams);
                cp.Type = popup ? SAPbouiCOM.BoMenuType.mt_POPUP : SAPbouiCOM.BoMenuType.mt_STRING;
                cp.UniqueID = id; cp.String = caption; cp.Enabled = enabled; cp.Position = subs.Count;
                subs.AddEx(cp);
            }
            finally { Rel(cp); Rel(subs); Rel(parent); }
        }

        private static void Rel(object o) { try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { } }
    }

    /// <summary>
    /// Right-click menu of the A/R Invoice form: builds the menu for the form under the cursor, re-checks that the same document is still active
    /// when an item is chosen, and runs the chosen action in the background. Identification is always by DocEntry.
    /// </summary>
    public sealed class InvoiceContextMenu
    {
        private readonly AddonServices _s;
        private readonly CtxMenuBuilder _builder;
        private readonly ActionGate _gate = new ActionGate();
        private readonly StatusCache _cache = new StatusCache(TimeSpan.FromSeconds(30));
        private readonly InvoiceActions _actions;
        private InvoiceContext _ctx;
        private bool _building;        // recursion guard: building the menu must never trigger another build

        public InvoiceContextMenu(AddonServices s)
        {
            _s = s; _builder = new CtxMenuBuilder(new SapMenuHost(s.App));
            _actions = new InvoiceActions(s, _cache);
        }

        public void RemoveAll() { try { _builder.Hide(); } catch (Exception ex) { Logger.Warn("ctx.hide.failed", "error", ex.Message); } }

        // ---------------------------------------------------------------- SAP events
        public void OnRightClick(SAPbouiCOM.ContextMenuInfo e)
        {
            if (_building) return;
            _building = true;
            try
            {
                if (e.BeforeAction) Build(e.FormUID);
                else _builder.Hide();           // standard SAP pattern: remove the dynamic items after the menu was shown
            }
            catch (Exception ex) { Logger.Error("ctx.rightclick.failed", ex); }
            finally { _building = false; }
        }

        public void OnFormActivated(string formUid)
        {
            try
            {
                if (!_s.SchemaReady) return;
                string why;
                var ctx = InvoiceContextResolver.Resolve(Snapshot(formUid), _s.Cfg.SapDatabase, _s.HasConfig && _s.Cfg.Active, out why);
                if (ctx != null) Prime(ctx);
            }
            catch (Exception ex) { Logger.Warn("ctx.activate.failed", "error", ex.Message); }
        }

        public void OnFormClosed(string formUid)
        {
            if (_ctx != null && _ctx.FormUid == formUid) _ctx = null;        // never act on a document whose form is gone
        }

        public void OnMenu(string menuUid)
        {
            CtxAction action;
            if (!CtxMenuDefinition.TryParse(menuUid, out action)) return;
            if (_building) return;
            InvoiceContext ctx = _ctx;
            string why;
            FormSnapshot active = null;
            SAPbouiCOM.Form af = null;
            try { af = _s.App.Forms.ActiveForm; active = Snapshot(af.UniqueID); } catch (Exception ex) { Logger.Warn("ctx.active.failed", "error", ex.Message); }
            finally { if (af != null) try { Marshal.ReleaseComObject(af); } catch { } }
            if (!InvoiceContextResolver.IsSameDocument(ctx, active, out why)) { _s.Alert(why); return; }
            if (!_gate.TryEnter(ctx.DocEntry)) { _s.Status("Another E-Invoice / ERPNext action is still running for this invoice.", true); return; }
            try { _actions.Run(action, ctx, () => _gate.Leave(ctx.DocEntry)); }
            catch (Exception ex) { _gate.Leave(ctx.DocEntry); Logger.Error("ctx.action.failed", ex, "action", action); _s.Alert("The action could not be started: " + ex.Message); }
        }

        // ---------------------------------------------------------------- internals
        private void Build(string formUid)
        {
            string why;
            if (!_s.SchemaReady) { _builder.Hide(); _ctx = null; _s.Status("E-Invoice & ERPNext: SAP objects are out of date. Open ERPNext Connection Settings > Create SAP UDF/UDT, then log out and in again.", true); return; }
            var ctx = InvoiceContextResolver.Resolve(Snapshot(formUid), _s.Cfg.SapDatabase, _s.HasConfig && _s.Cfg.Active, out why);
            if (ctx == null) { _builder.Hide(); _ctx = null; return; }
            _ctx = ctx;
            var rec = _s.Store.GetRecord(ctx.DocEntry);
            var state = CtxStateCalculator.Compute(rec, _cache.Get(ctx.DocEntry), _s.Cfg.EInvoiceEnabled, _s.Cfg.EWayBillEnabled, DateTime.Now);
            if (_gate.IsBusy(ctx.DocEntry)) state.Enabled.Clear();
            _builder.Show(state);
            Prime(ctx, rec);
        }

        /// <summary>Reads the live ERPNext status in the background so the NEXT right-click reflects it (actions re-check live anyway).</summary>
        private void Prime(InvoiceContext ctx, SyncRecord rec = null)
        {
            rec = rec ?? _s.Store.GetRecord(ctx.DocEntry);
            if (rec == null || rec.Status != SyncStatus.Synced || string.IsNullOrEmpty(rec.ErpInvoice)) return;
            if (!_cache.NeedsRefresh(ctx.DocEntry, DateTime.Now)) return;
            _cache.Put(ctx.DocEntry, _cache.Get(ctx.DocEntry));             // stamp: avoids parallel refreshes
            string inv = rec.ErpInvoice; int de = ctx.DocEntry;
            _s.Background("ctx-prime", () => _s.NewGst().GetStatus(inv, false), st => _cache.Put(de, st), ex => { _cache.Invalidate(de); Logger.Warn("ctx.prime.failed", "error", ex.Message); });
        }

        private FormSnapshot Snapshot(string formUid)
        {
            SAPbouiCOM.Form f = null; SAPbouiCOM.DBDataSources sources = null; SAPbouiCOM.DBDataSource ds = null;
            try
            {
                f = _s.App.Forms.Item(formUid);
                var snap = new FormSnapshot { FormUid = formUid, FormType = f.TypeEx, Database = _s.Sap.DatabaseName, Mode = f.Mode == SAPbouiCOM.BoFormMode.fm_OK_MODE ? "OK" : "Other" };
                if (snap.FormType != InvoiceContextResolver.ArInvoiceFormType) return snap;
                sources = f.DataSources.DBDataSources;
                ds = sources.Item("OINV");
                long de; int dn;
                snap.DocEntry = long.TryParse((ds.GetValue("DocEntry", 0) ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out de) ? de : 0;
                snap.DocNum = int.TryParse((ds.GetValue("DocNum", 0) ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dn) ? dn : 0;
                snap.ObjType = (ds.GetValue("ObjType", 0) ?? "").Trim();
                return snap;
            }
            catch (Exception ex) { Logger.Warn("ctx.snapshot.failed", "error", ex.Message); return null; }
            finally
            {
                foreach (var o in new object[] { ds, sources, f }) try { if (o != null) Marshal.ReleaseComObject(o); } catch { }
            }
        }
    }

    /// <summary>Individual invoice operations. Every action re-reads ERPNext live before it does anything; network work runs off the UI thread.</summary>
    public sealed class InvoiceActions
    {
        private readonly AddonServices _s;
        private readonly StatusCache _cache;
        private static readonly string[] Reasons = GstService.CancelReasons;
        private static readonly string[] VehicleReasons = { "Due to Break Down", "Due to Trans Shipment", "First Time", "Others" };

        public InvoiceActions(AddonServices s, StatusCache cache) { _s = s; _cache = cache; }

        private sealed class Op
        {
            public InvoiceContext Ctx; public SyncRecord Rec; public Action Release; private bool _done;
            public void Finish() { if (_done) return; _done = true; Release(); }
        }

        // ---------------------------------------------------------------- plumbing
        public void Run(CtxAction a, InvoiceContext ctx, Action release)
        {
            var op = new Op { Ctx = ctx, Release = release, Rec = _s.Store.GetRecord(ctx.DocEntry) };
            try
            {
                if (a == CtxAction.Sync) { Sync(op); return; }
                if (a == CtxAction.History) { History(op); return; }
                if (op.Rec == null || op.Rec.Status != SyncStatus.Synced || string.IsNullOrEmpty(op.Rec.ErpInvoice))
                { Dialogs.Message("E-Invoice & E-Way Bill", "This invoice is not synchronized with ERPNext yet. Use 'Synchronize with ERPNext' first.", true); op.Finish(); return; }
                bool mutating = a == CtxAction.Submit || a == CtxAction.GenerateEInv || a == CtxAction.CancelEInv || a == CtxAction.GenerateEwb || a == CtxAction.UpdateVehicle || a == CtxAction.UpdateTransporter || a == CtxAction.CancelEwb;
                if (mutating && !Authorization.CanRunGst(_s)) { Dialogs.Message("Not authorised", "Your SAP user is not authorised for this action in the active configuration.", true); op.Finish(); return; }
                switch (a)
                {
                    case CtxAction.ViewErp: ViewErp(op); break;
                    case CtxAction.Submit: Submit(op); break;
                    case CtxAction.ValidateEInv: ValidateEInv(op); break;
                    case CtxAction.GenerateEInv: GenerateEInv(op); break;
                    case CtxAction.ViewIrn: ViewIrn(op); break;
                    case CtxAction.ViewQr: Bg(op, "View QR", g => g.GetStatus(op.Rec.ErpInvoice, true), s => { Dialogs.Qr(s); op.Finish(); }); break;
                    case CtxAction.DownloadSigned: Bg(op, "Download signed invoice", g => g.GetStatus(op.Rec.ErpInvoice, true), s => { Dialogs.SaveText("signed-einvoice-" + Safe(s.SalesInvoice) + ".json", s.SignedInvoice); op.Finish(); }); break;
                    case CtxAction.PrintEInv: Bg(op, "Print e-Invoice", g => g.DownloadInvoicePdf(op.Rec.ErpInvoice), b => { Dialogs.SaveOrOpenBytes("einvoice-" + Safe(op.Rec.ErpInvoice) + ".pdf", b, true); op.Finish(); }); break;
                    case CtxAction.RefreshIrn: case CtxAction.RefreshEwb: Refresh(op, a == CtxAction.RefreshEwb); break;
                    case CtxAction.CancelEInv: CancelEInv(op); break;
                    case CtxAction.ValidateEwb: GenerateEwb(op, validateOnly: true); break;
                    case CtxAction.GenerateEwb: GenerateEwb(op, validateOnly: false); break;
                    case CtxAction.ViewEwb: ViewEwb(op); break;
                    case CtxAction.UpdateVehicle: UpdateVehicle(op); break;
                    case CtxAction.UpdateTransporter: UpdateTransporter(op); break;
                    case CtxAction.CancelEwb: CancelEwb(op); break;
                    case CtxAction.DownloadEwb: Bg(op, "Download e-Way Bill", g => g.DownloadEWayBillPdf(op.Rec.ErpInvoice), b => { Dialogs.SaveOrOpenBytes("eway-bill-" + Safe(op.Rec.ErpInvoice) + ".pdf", b, false); op.Finish(); }); break;
                    case CtxAction.PrintEwb: Bg(op, "Print e-Way Bill", g => g.DownloadEWayBillPdf(op.Rec.ErpInvoice), b => { Dialogs.SaveOrOpenBytes("eway-bill-" + Safe(op.Rec.ErpInvoice) + ".pdf", b, true); op.Finish(); }); break;
                }
            }
            catch (Exception ex) { Fail(op, a.ToString(), ex); }
        }

        private static string Safe(string s) { return (s ?? "x").Replace('/', '-').Replace('\\', '-'); }

        private void Working(string text) { _s.Status(text + " ...", false); }

        private void Fail(Op op, string what, Exception ex)
        {
            Logger.Error("ctx.action.error", ex, "action", what, "docEntry", op.Ctx.DocEntry);
            Dialogs.Message("E-Invoice & E-Way Bill - " + what, ex.Message, true);
            op.Finish();
        }

        /// <summary>Background work with the add-on's GstService; the callback runs on the UI thread. Failures always release the invoice.</summary>
        private void Bg<T>(Op op, string what, Func<GstService, T> work, Action<T> done)
        {
            Working(what);
            _s.Background(what, () => work(_s.NewGst()), r => { try { done(r); } catch (Exception ex) { Fail(op, what, ex); } }, ex => Fail(op, what, ex));
        }

        /// <summary>Stores the real ERPNext state in SAP (UDT + invoice UDFs through the DI API), refreshes the cache and the open invoice screen.</summary>
        private void Persist(Op op, GstStatus s, string error = null)
        {
            try
            {
                var rec = _s.NewGst().ToRecord(op.Ctx.DocEntry, op.Ctx.DocNum, s);
                rec.Error = error;
                _s.GstStore.SaveGst(rec);
            }
            catch (Exception ex) { Logger.Warn("ctx.persist.failed", "error", ex.Message); }
            if (s != null) _cache.Put(op.Ctx.DocEntry, s);
            RefreshInvoiceScreen(op.Ctx);
        }

        private void RefreshInvoiceScreen(InvoiceContext ctx)
        {
            SAPbouiCOM.Form af = null;
            try { af = _s.App.Forms.ActiveForm; if (af.UniqueID == ctx.FormUid) _s.App.ActivateMenuItem("1304"); }      // 1304 = Refresh: reloads the UDF values just written
            catch (Exception ex) { Logger.Warn("ctx.refresh.failed", "error", ex.Message); }
            finally { if (af != null) try { Marshal.ReleaseComObject(af); } catch { } }
        }

        private bool UncertainPending(Op op)
        {
            if (_cache.IsUncertain(op.Ctx.DocEntry)) return true;
            var g = _s.GstStore.GetGst(op.Ctx.DocEntry);
            return g != null && (g.Error ?? "").StartsWith("UNCERTAIN", StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- ERPNext group
        private void Sync(Op op)
        {
            if (op.Rec != null && op.Rec.Status == SyncStatus.Synced) { Dialogs.Message("Synchronize with ERPNext", "Already synchronized as " + op.Rec.ErpInvoice + ".\nNo new ERPNext invoice is created."); op.Finish(); return; }
            _s.Coordinator.Retry(op.Ctx.DocEntry);       // idempotent: ERPNext is checked for an existing invoice first
            _s.Status("Invoice queued for ERPNext synchronization (DocEntry " + op.Ctx.DocEntry + "). The result appears in the status bar.", false);
            op.Finish();
        }

        private void ViewErp(Op op)
        {
            string url = _s.Cfg.ErpUrl.TrimEnd('/') + "/app/sales-invoice/" + Uri.EscapeDataString(op.Rec.ErpInvoice);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            op.Finish();
        }

        private void History(Op op)
        {
            var sb = new StringBuilder();
            var r = op.Rec;
            sb.AppendLine("SAP invoice DocEntry " + op.Ctx.DocEntry + " (DocNum " + op.Ctx.DocNum + ")  |  configuration: " + _s.Cfg.Name);
            if (r == null) sb.AppendLine("No synchronization record yet.");
            else sb.AppendLine("Sync status: " + r.Status + "   ERPNext invoice: " + r.ErpInvoice + "   retries: " + r.RetryCount + "   last attempt: " + (r.SyncDate.HasValue ? r.SyncDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-") + (string.IsNullOrEmpty(r.Error) ? "" : "\nMessage: " + r.Error));
            var g = _s.GstStore.GetGst(op.Ctx.DocEntry);
            if (g != null) sb.AppendLine("\nE-Invoice: " + g.EInvStatus + "  IRN " + g.Irn + "  Ack " + g.AckNo + " " + g.AckDate + "\nE-Way Bill: " + g.EwbStatus + "  No " + g.EwbNo + "  valid until " + g.EwbValid + "\nLast GST refresh: " + g.LastSync + (string.IsNullOrEmpty(g.Error) ? "" : "\nNote: " + g.Error));
            sb.AppendLine("\nRecent log lines for this invoice:");
            try
            {
                string tag1 = "docEntry=" + op.Ctx.DocEntry, tag2 = r == null || string.IsNullOrEmpty(r.ErpInvoice) ? "\u0001" : r.ErpInvoice;
                foreach (var file in new DirectoryInfo(Logger.Directory).GetFiles("einvgst-*.log").OrderByDescending(f => f.Name).Take(3))
                    foreach (var line in File.ReadAllLines(file.FullName).Where(l => l.Contains(tag1 + " ") || l.EndsWith(tag1) || l.Contains(tag2)).Reverse().Take(25)) sb.AppendLine(line.Length > 220 ? line.Substring(0, 220) : line);
            }
            catch (Exception ex) { sb.AppendLine("(log not readable: " + ex.Message + ")"); }
            Dialogs.Info("Integration History", sb.ToString());
            op.Finish();
        }

        private void Submit(Op op)
        {
            Bg(op, "Reading ERPNext status", g => g.GetStatus(op.Rec.ErpInvoice, false), s =>
            {
                _cache.Put(op.Ctx.DocEntry, s);
                if (s.DocStatus == 1) { Dialogs.Message("Submit Invoice in ERPNext", op.Rec.ErpInvoice + " is already " + s.DocStatusText + ". Nothing was submitted."); op.Finish(); return; }
                if (s.DocStatus == 2) { Dialogs.Message("Submit Invoice in ERPNext", op.Rec.ErpInvoice + " is cancelled in ERPNext and cannot be submitted.", true); op.Finish(); return; }
                if (!Dialogs.Confirm("Submit Invoice in ERPNext", "Submit Sales Invoice " + s.SalesInvoice + " (" + s.CustomerName + ", total " + s.GrandTotal.ToString("N2", CultureInfo.InvariantCulture) + ") in ERPNext?\n\nA submitted invoice is posted to the ERPNext books and can no longer be edited. This is never done automatically.")) { op.Finish(); return; }
                Bg(op, "Submitting in ERPNext", g => g.SubmitInvoice(op.Rec.ErpInvoice), r =>
                {
                    if (r.Status != null) Persist(op, r.Status);
                    Dialogs.Message("Submit Invoice in ERPNext", (r.Success ? "Done: " : "NOT submitted: ") + r.Message, !r.Success);
                    op.Finish();
                });
            });
        }

        // ---------------------------------------------------------------- E-Invoice group
        private void ValidateEInv(Op op)
        {
            Bg(op, "Validating e-Invoice", g => g.ValidateEInvoiceReport(op.Rec.ErpInvoice), list =>
            {
                Dialogs.Checks("Validate E-Invoice - " + op.Rec.ErpInvoice, list, "Nothing was generated by this validation.");
                op.Finish();
            });
        }

        private void GenerateEInv(Op op)
        {
            if (UncertainPending(op))
            {
                Dialogs.Message("Generate E-Invoice", "A previous generation attempt for this invoice ended with an UNCERTAIN result.\n\nUse 'Refresh IRN Status' first. Generation stays blocked until the status could be read from ERPNext.", true);
                op.Finish(); return;
            }
            Bg(op, "Validating e-Invoice", g => g.ValidateEInvoiceReport(op.Rec.ErpInvoice), list =>
            {
                var bad = list.Where(i => !i.Ok).ToList();
                if (bad.Count > 0) { Dialogs.Checks("Generate E-Invoice - cannot continue", list, "Fix the failed checks, then try again."); op.Finish(); return; }
                string env = _s.Cfg.Environment;
                if (!Dialogs.Confirm("Generate E-Invoice", "Generate the e-Invoice (IRN) for " + op.Rec.ErpInvoice + " (SAP DocNum " + op.Ctx.DocNum + ")?\n\nERPNext will call the " + env.ToUpperInvariant() + " IRP." + (env == "Production" ? "\nThis creates a REAL IRN on the government portal." : "") + "\nCompany: " + _s.Cfg.ErpCompany)) { op.Finish(); return; }
                Bg(op, "Generating e-Invoice", g => g.GenerateEInvoice(op.Rec.ErpInvoice), r =>
                {
                    if (r.Uncertain) { _cache.MarkUncertain(op.Ctx.DocEntry, true); if (r.Status != null) Persist(op, r.Status, "UNCERTAIN: " + r.Message); }
                    else { _cache.MarkUncertain(op.Ctx.DocEntry, false); if (r.Status != null) Persist(op, r.Status); }
                    if (r.Success && r.Status != null)
                        Dialogs.Info("E-Invoice generated", "IRN: " + r.Status.Irn + "\nAck No: " + r.Status.AckNo + "\nAck Date: " + (r.Status.AckDate.HasValue ? r.Status.AckDate.Value.ToString("yyyy-MM-dd HH:mm") : "") + "\n\nThe IRN came from the IRP through ERPNext and is stored in SAP.");
                    else Dialogs.Message("Generate E-Invoice", (r.Uncertain ? "UNCERTAIN: " : "NOT generated: ") + r.Message, true);
                    op.Finish();
                });
            });
        }

        private void ViewIrn(Op op)
        {
            Bg(op, "Reading IRN details", g => g.GetStatus(op.Rec.ErpInvoice, true), s =>
            {
                _cache.Put(op.Ctx.DocEntry, s);
                string text = "SAP Invoice Number: " + op.Ctx.DocNum + "  (DocEntry " + op.Ctx.DocEntry + ")\nERPNext Invoice ID: " + s.SalesInvoice + "  (" + s.DocStatusText + ")\n\nIRN: " + s.Irn +
                    "\nAcknowledgement Number: " + s.AckNo + "\nAcknowledgement Date: " + (s.AckDate.HasValue ? s.AckDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : "") +
                    "\nIRN Status: " + (s.HasIrn ? "Generated" : (s.IrnCancelled ? "Cancelled" : "Not generated")) + "\nCancellation Status: " + (s.IrnCancelled ? "Cancelled" + (s.IrnCancelledOn.HasValue ? " on " + s.IrnCancelledOn.Value.ToString("yyyy-MM-dd HH:mm") : "") : "Not cancelled");
                Persist(op, s);
                Dialogs.Info("IRN Details - " + s.SalesInvoice, text,
                    new Dialogs.Btn { Caption = "Copy IRN", Click = () => { if (!string.IsNullOrEmpty(s.Irn)) System.Windows.Forms.Clipboard.SetText(s.Irn); } },
                    new Dialogs.Btn { Caption = "View QR Code", Click = () => Dialogs.Qr(s) },
                    new Dialogs.Btn { Caption = "Download Signed", Click = () => Dialogs.SaveText("signed-einvoice-" + Safe(s.SalesInvoice) + ".json", s.SignedInvoice) },
                    new Dialogs.Btn { Caption = "Print E-Invoice", Click = () => { var b = _s.NewGst().DownloadInvoicePdf(s.SalesInvoice); Dialogs.SaveOrOpenBytes("einvoice-" + Safe(s.SalesInvoice) + ".pdf", b, true); } },
                    new Dialogs.Btn { Caption = "Refresh Status", Click = () => { var n = _s.NewGst().GetStatus(s.SalesInvoice, true); Persist(op, n); Dialogs.Message("IRN status", "Refreshed: " + (n.HasIrn ? "IRN " + n.Irn : "no active IRN")); } });
                op.Finish();
            });
        }

        private void Refresh(Op op, bool ewb)
        {
            Bg(op, ewb ? "Refreshing e-Way Bill status" : "Refreshing IRN status", g =>
            {
                var before = g.GetStatus(op.Rec.ErpInvoice, true);
                if (ewb && before.HasEwb) { var r = g.RefreshEWayBill(op.Rec.ErpInvoice); return r.Status ?? before; }     // re-reads the portal data of the EXISTING e-Way Bill; generates nothing
                return before;
            }, s =>
            {
                _cache.MarkUncertain(op.Ctx.DocEntry, false);            // the status could be read: the uncertainty is reconciled
                Persist(op, s);
                string msg = ewb ? "E-Way Bill: " + s.EwbStatus + (s.HasEwb ? "  No " + s.EwbNo + "  valid until " + (s.EwbValidUpto.HasValue ? s.EwbValidUpto.Value.ToString("yyyy-MM-dd HH:mm") : "?") : "")
                                 : "E-Invoice: " + (s.HasIrn ? "Generated  IRN " + s.Irn : (s.IrnCancelled ? "Cancelled" : "Not generated"));
                _s.Status("Refreshed from ERPNext. " + msg, false);
                Dialogs.Message(ewb ? "Refresh E-Way Bill Status" : "Refresh IRN Status", msg + "\nERPNext invoice: " + s.SalesInvoice + " (" + s.DocStatusText + ")\nNothing was generated by this refresh.");
                op.Finish();
            });
        }

        private void CancelEInv(Op op)
        {
            Bg(op, "Checking cancellation eligibility", g => { var s = g.GetStatus(op.Rec.ErpInvoice, false); return Tuple.Create(s, g.ValidateEInvoiceCancel(s, Reasons[0])); }, t =>
            {
                var errs = t.Item2.Errors.Where(e => !e.Contains("reason")).ToList();
                if (errs.Count > 0) { Dialogs.Message("Cancel E-Invoice", string.Join("\n", errs), true); op.Finish(); return; }
                var vals = Dialogs.Input("Cancel E-Invoice - " + t.Item1.SalesInvoice,
                    "IRN " + t.Item1.Irn + "\nCancelling the IRN also CANCELS the ERPNext Sales Invoice (and any active E-Way Bill). The SAP invoice is not changed or deleted.",
                    new[] { new Dialogs.Field { Key = "reason", Label = "Cancellation reason", Options = Reasons, Default = Reasons[2] }, new Dialogs.Field { Key = "remark", Label = "Remarks" } }, "Cancel E-Invoice", null);
                if (vals == null) { op.Finish(); return; }
                if (!Dialogs.Confirm("Cancel E-Invoice", "Cancel the e-Invoice of " + t.Item1.SalesInvoice + " on the " + _s.Cfg.Environment.ToUpperInvariant() + " IRP?\nReason: " + vals["reason"])) { op.Finish(); return; }
                Bg(op, "Cancelling e-Invoice", g => g.CancelEInvoice(op.Rec.ErpInvoice, vals["reason"], vals["remark"]), r =>
                {
                    if (r.Status != null) Persist(op, r.Status);
                    Dialogs.Message("Cancel E-Invoice", (r.Success ? "Cancelled (confirmed by ERPNext): " : "NOT cancelled: ") + r.Message, !r.Success);
                    op.Finish();
                });
            });
        }

        // ---------------------------------------------------------------- E-Way Bill group
        private static decimal Dec(string s) { decimal d; decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d); return d; }

        private static EWayBillInput ToInput(Dictionary<string, string> v)
        {
            return new EWayBillInput { TransporterId = v["tid"].ToUpperInvariant(), TransporterName = v["tname"], VehicleNo = v["veh"], ModeOfTransport = v["mode"], Distance = Dec(v["dist"]), LrNo = v["lr"], LrDate = v["lrd"] };
        }

        private void GenerateEwb(Op op, bool validateOnly)
        {
            Bg(op, "Reading ERPNext status", g => g.GetStatus(op.Rec.ErpInvoice, false), s =>
            {
                _cache.Put(op.Ctx.DocEntry, s);
                if (s.HasEwb) { Dialogs.Message("E-Way Bill", "An E-Way Bill already exists for this invoice (" + s.EwbNo + "). Use View E-Way Bill Details.", true); op.Finish(); return; }
                var fields = new[]
                {
                    new Dialogs.Field { Key = "tid", Label = "Transporter ID (GSTIN)", Default = s.TransporterId }, new Dialogs.Field { Key = "tname", Label = "Transporter Name", Default = s.TransporterName },
                    new Dialogs.Field { Key = "veh", Label = "Vehicle Number", Default = s.VehicleNo }, new Dialogs.Field { Key = "mode", Label = "Transport Mode", Options = new[] { "Road", "Rail", "Air", "Ship" }, Default = string.IsNullOrEmpty(s.ModeOfTransport) ? "Road" : s.ModeOfTransport },
                    new Dialogs.Field { Key = "dist", Label = "Distance (km, 0 = auto)", Default = s.Distance > 0 ? s.Distance.ToString("0", CultureInfo.InvariantCulture) : "" },
                    new Dialogs.Field { Key = "lr", Label = "Transport Document Number", Default = s.LrNo }, new Dialogs.Field { Key = "lrd", Label = "Transport Document Date (yyyy-mm-dd)", Default = s.LrDate }
                };
                var validate = new Dialogs.Btn
                {
                    Caption = "Validate",
                    Click = () =>
                    {
                        var input = ToInput(Dialogs.CurrentCollector());
                        _s.Background("validate-ewb", () => _s.NewGst().ValidateEWayBillReport(op.Rec.ErpInvoice, input), list => Dialogs.Checks("Validate E-Way Bill - " + op.Rec.ErpInvoice, list, "Nothing was generated."), ex => Dialogs.Message("Validate E-Way Bill", ex.Message, true));
                    }
                };
                if (validateOnly)
                {
                    var input0 = Dialogs.Input("Validate E-Way Bill - " + s.SalesInvoice, "Enter the transport details to validate. Nothing is generated.", fields, "Validate", null);
                    if (input0 != null) _s.Background("validate-ewb", () => _s.NewGst().ValidateEWayBillReport(op.Rec.ErpInvoice, ToInput(input0)), list => { Dialogs.Checks("Validate E-Way Bill - " + op.Rec.ErpInvoice, list, "Nothing was generated."); op.Finish(); }, ex => Fail(op, "Validate E-Way Bill", ex));
                    else op.Finish();
                    return;
                }
                var vals = Dialogs.Input("Generate E-Way Bill - " + s.SalesInvoice, "Invoice " + s.SalesInvoice + " (SAP DocNum " + op.Ctx.DocNum + "), customer " + s.CustomerName + ", total " + s.GrandTotal.ToString("N2", CultureInfo.InvariantCulture), fields, "Generate E-Way Bill", null, validate);
                if (vals == null) { op.Finish(); return; }
                var inp = ToInput(vals);
                Bg(op, "Validating e-Way Bill", g => g.ValidateEWayBillReport(op.Rec.ErpInvoice, inp), list =>
                {
                    if (list.Any(i => !i.Ok)) { Dialogs.Checks("Generate E-Way Bill - cannot continue", list, "Fix the failed checks and try again."); op.Finish(); return; }
                    if (!Dialogs.Confirm("Generate E-Way Bill", "Generate the E-Way Bill for " + op.Rec.ErpInvoice + "?\nVehicle " + inp.VehicleNo + ", " + inp.ModeOfTransport + ", " + inp.Distance + " km.\nERPNext will call the " + _s.Cfg.Environment.ToUpperInvariant() + " e-Way Bill system." + (_s.Cfg.Environment == "Production" ? "\nThis creates a REAL E-Way Bill." : ""))) { op.Finish(); return; }
                    Bg(op, "Generating e-Way Bill", g => g.GenerateEWayBill(op.Rec.ErpInvoice, inp), r =>
                    {
                        if (r.Status != null) Persist(op, r.Status, r.Uncertain ? "UNCERTAIN: " + r.Message : null);
                        if (r.Success && r.Status != null) Dialogs.Info("E-Way Bill generated", "E-Way Bill No: " + r.Status.EwbNo + "\nDate: " + (r.Status.EwbDate.HasValue ? r.Status.EwbDate.Value.ToString("yyyy-MM-dd HH:mm") : "") + "\nValid until: " + (r.Status.EwbValidUpto.HasValue ? r.Status.EwbValidUpto.Value.ToString("yyyy-MM-dd HH:mm") : "") + "\n\nReturned by the e-Way Bill system through ERPNext and stored in SAP.");
                        else Dialogs.Message("Generate E-Way Bill", (r.Uncertain ? "UNCERTAIN: " : "NOT generated: ") + r.Message, true);
                        op.Finish();
                    });
                });
            });
        }

        private void ViewEwb(Op op)
        {
            Bg(op, "Reading e-Way Bill details", g => g.GetStatus(op.Rec.ErpInvoice, false), s =>
            {
                Persist(op, s);
                string text = "SAP Invoice Number: " + op.Ctx.DocNum + "  (DocEntry " + op.Ctx.DocEntry + ")\nERPNext Invoice ID: " + s.SalesInvoice + "\n\nE-Way Bill Number: " + s.EwbNo + "\nStatus: " + s.EwbStatus +
                    "\nGenerated: " + (s.EwbDate.HasValue ? s.EwbDate.Value.ToString("yyyy-MM-dd HH:mm") : "") + "\nValid until: " + (s.EwbValidUpto.HasValue ? s.EwbValidUpto.Value.ToString("yyyy-MM-dd HH:mm") : "") +
                    "\n\nTransporter ID: " + s.TransporterId + "\nTransporter: " + s.TransporterName + "\nVehicle: " + s.VehicleNo + "\nMode: " + s.ModeOfTransport + "\nDistance: " + s.Distance + " km\nTransport Doc: " + s.LrNo + " " + s.LrDate;
                Dialogs.Info("E-Way Bill - " + s.SalesInvoice, text,
                    new Dialogs.Btn { Caption = "Download PDF", Click = () => Dialogs.SaveOrOpenBytes("eway-bill-" + s.EwbNo + ".pdf", _s.NewGst().DownloadEWayBillPdf(s.SalesInvoice), false) },
                    new Dialogs.Btn { Caption = "Print", Click = () => Dialogs.SaveOrOpenBytes("eway-bill-" + s.EwbNo + ".pdf", _s.NewGst().DownloadEWayBillPdf(s.SalesInvoice), true) });
                op.Finish();
            });
        }

        private void UpdateVehicle(Op op)
        {
            Bg(op, "Reading ERPNext status", g => g.GetStatus(op.Rec.ErpInvoice, false), s =>
            {
                if (!s.HasEwb) { Dialogs.Message("Update Vehicle Number", "There is no active E-Way Bill for this invoice.", true); op.Finish(); return; }
                var vals = Dialogs.Input("Update Vehicle Number - " + s.EwbNo, "The update is sent to the e-Way Bill system through ERPNext.",
                    new[]
                    {
                        new Dialogs.Field { Key = "ewb", Label = "E-Way Bill Number", Default = s.EwbNo, ReadOnly = true }, new Dialogs.Field { Key = "old", Label = "Existing Vehicle Number", Default = s.VehicleNo, ReadOnly = true },
                        new Dialogs.Field { Key = "veh", Label = "New Vehicle Number" }, new Dialogs.Field { Key = "reason", Label = "Update Reason", Options = VehicleReasons, Default = VehicleReasons[0] },
                        new Dialogs.Field { Key = "place", Label = "Place" }, new Dialogs.Field { Key = "state", Label = "State (e.g. Maharashtra)" }, new Dialogs.Field { Key = "remark", Label = "Remarks" }
                    }, "Update Vehicle",
                    v => { if (v["veh"].Length == 0 || v["place"].Length == 0 || v["state"].Length == 0) { Dialogs.Message("Update Vehicle Number", "New vehicle number, place and state are required.", true); return false; } return true; });
                if (vals == null) { op.Finish(); return; }
                var inp = new EWayBillInput { VehicleNo = vals["veh"], ModeOfTransport = string.IsNullOrEmpty(s.ModeOfTransport) ? "Road" : s.ModeOfTransport, VehicleType = string.IsNullOrEmpty(s.VehicleType) ? "Regular" : s.VehicleType, LrNo = s.LrNo, LrDate = s.LrDate, Distance = s.Distance };
                Bg(op, "Updating vehicle", g => g.UpdateVehicle(op.Rec.ErpInvoice, inp, vals["reason"], vals["remark"], vals["place"], vals["state"]), r =>
                {
                    if (r.Status != null) Persist(op, r.Status);
                    Dialogs.Message("Update Vehicle Number", (r.Success ? "Done: " : "NOT updated: ") + r.Message, !r.Success);
                    op.Finish();
                });
            });
        }

        private void UpdateTransporter(Op op)
        {
            Bg(op, "Reading ERPNext status", g => g.GetStatus(op.Rec.ErpInvoice, false), s =>
            {
                if (!s.HasEwb) { Dialogs.Message("Update Transporter", "There is no active E-Way Bill for this invoice.", true); op.Finish(); return; }
                var vals = Dialogs.Input("Update Transporter - " + s.EwbNo, "The transporter is identified by its GSTIN. The name is informational: ERPNext takes it from the Supplier master.",
                    new[]
                    {
                        new Dialogs.Field { Key = "ewb", Label = "E-Way Bill Number", Default = s.EwbNo, ReadOnly = true }, new Dialogs.Field { Key = "old", Label = "Existing Transporter", Default = (s.TransporterName + " " + s.TransporterId).Trim(), ReadOnly = true },
                        new Dialogs.Field { Key = "tid", Label = "New Transporter GSTIN" }, new Dialogs.Field { Key = "tname", Label = "New Transporter Name (info)" }
                    }, "Update Transporter",
                    v => { if (v["tid"].Length == 0) { Dialogs.Message("Update Transporter", "The new transporter GSTIN is required.", true); return false; } return true; });
                if (vals == null) { op.Finish(); return; }
                Bg(op, "Updating transporter", g => g.UpdateTransporter(op.Rec.ErpInvoice, vals["tid"].ToUpperInvariant()), r =>
                {
                    if (r.Status != null) Persist(op, r.Status);
                    Dialogs.Message("Update Transporter", (r.Success ? "Done: " : "NOT updated: ") + r.Message, !r.Success);
                    op.Finish();
                });
            });
        }

        private void CancelEwb(Op op)
        {
            Bg(op, "Checking cancellation eligibility", g => { var s = g.GetStatus(op.Rec.ErpInvoice, false); return Tuple.Create(s, g.ValidateEWayBillCancel(s, Reasons[0])); }, t =>
            {
                var errs = t.Item2.Errors.Where(e => !e.Contains("reason")).ToList();
                if (errs.Count > 0) { Dialogs.Message("Cancel E-Way Bill", string.Join("\n", errs), true); op.Finish(); return; }
                var vals = Dialogs.Input("Cancel E-Way Bill - " + t.Item1.EwbNo, "E-Way Bill " + t.Item1.EwbNo + " of invoice " + t.Item1.SalesInvoice + ". Only the E-Way Bill is cancelled.",
                    new[] { new Dialogs.Field { Key = "reason", Label = "Cancellation reason", Options = Reasons, Default = Reasons[2] }, new Dialogs.Field { Key = "remark", Label = "Remarks" } }, "Cancel E-Way Bill", null);
                if (vals == null) { op.Finish(); return; }
                if (!Dialogs.Confirm("Cancel E-Way Bill", "Cancel E-Way Bill " + t.Item1.EwbNo + " on the " + _s.Cfg.Environment.ToUpperInvariant() + " e-Way Bill system?\nReason: " + vals["reason"])) { op.Finish(); return; }
                Bg(op, "Cancelling e-Way Bill", g => g.CancelEWayBill(op.Rec.ErpInvoice, vals["reason"], vals["remark"]), r =>
                {
                    if (r.Status != null) Persist(op, r.Status);
                    Dialogs.Message("Cancel E-Way Bill", (r.Success ? "Cancelled (confirmed by ERPNext): " : "NOT cancelled: ") + r.Message, !r.Success);
                    op.Finish();
                });
            });
        }
    }
}
