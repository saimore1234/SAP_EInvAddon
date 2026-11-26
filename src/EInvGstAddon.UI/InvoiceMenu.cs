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
using Newtonsoft.Json.Linq;

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

        private bool Configured { get { return _s.HasConfig && _s.Cfg.Active && !string.IsNullOrEmpty(_s.Cfg.IntegrationId); } }

        // ---------------------------------------------------------------- SAP events
        public void OnRightClick(SAPbouiCOM.ContextMenuInfo e)
        {
            if (_building) return;
            _building = true;
            try
            {
                if (e.BeforeAction) { Logger.Info("ctx.rightclick", "form", e.FormUID, "item", e.ItemUID, "row", e.Row, "col", e.ColUID); Build(e.FormUID); }
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
                var ctx = InvoiceContextResolver.Resolve(Snapshot(formUid), _s.Cfg.SapDatabase, Configured, out why);
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
            RunAction(action, ctx);
        }

        /// <summary>Also used by the E-Invoice / E-Way Bill screens: same checks, same gate, same actions.</summary>
        public void RunAction(CtxAction action, InvoiceContext ctx)
        {
            if (!_gate.TryEnter(ctx.DocEntry)) { _s.Status("Another GST action is still running for this invoice.", true); return; }
            try { _actions.Run(action, ctx, () => _gate.Leave(ctx.DocEntry)); }
            catch (Exception ex) { _gate.Leave(ctx.DocEntry); Logger.Error("ctx.action.failed", ex, "action", action); _s.Alert("The action could not be started: " + ex.Message); }
        }

        // ---------------------------------------------------------------- internals
        private void Build(string formUid)
        {
            string why;
            if (!_s.SchemaReady) { _builder.Hide(); _ctx = null; _s.Status("E-Invoice & GST Portal: SAP objects are out of date. Open GST Portal Connection Settings > Create SAP UDF/UDT, then log out and in again.", true); return; }
            var ctx = InvoiceContextResolver.Resolve(Snapshot(formUid), _s.Cfg.SapDatabase, Configured, out why);
            if (ctx == null) { Logger.Info("ctx.menu.hidden", "reason", why); _builder.Hide(); _ctx = null; return; }
            _ctx = ctx;
            var state = CtxStateCalculator.Compute(_cache.Get(ctx.DocEntry), Configured, _s.Cfg.EInvoiceEnabled, _s.Cfg.EWayBillEnabled, DateTime.Now);
            if (_gate.IsBusy(ctx.DocEntry)) state.Enabled.Clear();
            _builder.Show(state);
            Prime(ctx);
        }

        /// <summary>Reads the stored GST state from the server in the background so the NEXT right-click reflects it (actions re-check live anyway).</summary>
        private void Prime(InvoiceContext ctx)
        {
            if (!Configured || !_cache.NeedsRefresh(ctx.DocEntry, DateTime.Now)) return;
            _cache.Put(ctx.DocEntry, _cache.Get(ctx.DocEntry));             // stamp: avoids parallel refreshes
            int de = ctx.DocEntry, dn = ctx.DocNum;
            _s.Background("ctx-prime", () => _s.NewGateway().Status(de, dn, false), st => _cache.Put(de, st), ex => { _cache.Invalidate(de); Logger.Warn("ctx.prime.failed", "error", ex.Message); });
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

    /// <summary>
    /// Individual invoice operations. The invoice JSON is built on the UI thread from SAP (DI API); network calls run in the background and talk ONLY to the
    /// ERPNext GST API. Nothing is created in ERPNext besides the minimal "SAP GST Transaction" record the server keeps.
    /// </summary>
    public sealed class InvoiceActions
    {
        private readonly AddonServices _s;
        private readonly StatusCache _cache;
        private static readonly string[] Reasons = { "Duplicate", "Order Cancelled", "Data Entry Mistake", "Others" };
        private static readonly string[] VehicleReasons = { "Due to Break Down", "Due to Trans Shipment", "First Time", "Others" };

        public InvoiceActions(AddonServices s, StatusCache cache) { _s = s; _cache = cache; }

        private sealed class Op
        {
            public InvoiceContext Ctx; public Action Release; private bool _done;
            public GstSource Source; public JObject Contract;
            public void Finish() { if (_done) return; _done = true; Release(); }
        }

        // ---------------------------------------------------------------- plumbing
        public void Run(CtxAction a, InvoiceContext ctx, Action release)
        {
            var op = new Op { Ctx = ctx, Release = release };
            try
            {
                if (a == CtxAction.History) { ShowHistory(op); return; }
                if (!_s.HasConfig || string.IsNullOrEmpty(_s.Cfg.IntegrationId)) { Dialogs.Message("E-Invoice & E-Way Bill", "There is no active GST configuration for this SAP company.", true); op.Finish(); return; }
                bool mutating = a == CtxAction.GenerateEInv || a == CtxAction.CancelEInv || a == CtxAction.GenerateEwb || a == CtxAction.UpdateVehicle || a == CtxAction.UpdateTransporter || a == CtxAction.CancelEwb || a == CtxAction.SyncGst;
                if (mutating && !Authorization.CanRunGst(_s)) { Dialogs.Message("Not authorised", "Your SAP user is not authorised for this action in the active configuration.", true); op.Finish(); return; }
                switch (a)
                {
                    case CtxAction.ValidateGst: case CtxAction.ValidateEInv: Validate(op); break;
                    case CtxAction.ValidateEwb: GenerateEwb(op, validateOnly: true); break;
                    case CtxAction.SyncGst: Sync(op); break;
                    case CtxAction.RefreshGst: case CtxAction.RefreshIrn: case CtxAction.RefreshEwb: Refresh(op, a); break;
                    case CtxAction.GenerateEInv: GenerateEInv(op); break;
                    case CtxAction.ViewIrn: ViewIrn(op); break;
                    case CtxAction.ViewAck: ViewAck(op); break;
                    case CtxAction.ViewQr: Status(op, "View QR", s => { Dialogs.Qr(s); op.Finish(); }); break;
                    case CtxAction.DownloadSigned: Status(op, "Download signed data", s => { Dialogs.SaveText("signed-einvoice-" + _s.Cfg.SapDatabase + "-" + op.Ctx.DocNum + ".json", SignedText(s)); op.Finish(); }); break;
                    case CtxAction.PrintEInv: PrintEInv(op); break;
                    case CtxAction.CancelEInv: CancelEInv(op); break;
                    case CtxAction.GenerateEwb: GenerateEwb(op, validateOnly: false); break;
                    case CtxAction.ViewEwb: ViewEwb(op); break;
                    case CtxAction.UpdateVehicle: UpdateVehicle(op); break;
                    case CtxAction.UpdateTransporter: UpdateTransporter(op); break;
                    case CtxAction.DownloadEwb: PrintEwb(op, open: false); break;
                    case CtxAction.PrintEwb: PrintEwb(op, open: true); break;
                    case CtxAction.CancelEwb: CancelEwb(op); break;
                }
            }
            catch (Exception ex) { Fail(op, a.ToString(), ex); }
        }

        private void Fail(Op op, string what, Exception ex)
        {
            Logger.Error("ctx.action.error", ex, "action", what, "docEntry", op.Ctx.DocEntry);
            Dialogs.Message("E-Invoice & E-Way Bill - " + what, ex.Message, true);
            op.Finish();
        }

        private void Working(string text) { _s.Status(text + " ...", false); }

        private void Bg<T>(Op op, string what, Func<GstGateway, T> work, Action<T> done)
        {
            Working(what);
            _s.Background(what, () => work(_s.NewGateway()), r => { try { done(r); } catch (Exception ex) { Fail(op, what, ex); } }, ex => Fail(op, what, ex));
        }

        /// <summary>Reads the SAP invoice (DI API, UI thread) and builds the GST JSON. Problems that make a correct JSON impossible are shown, never guessed away.</summary>
        private bool Build(Op op, TransportInput transport, out BuildResult br)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            op.Source = _s.Source.GetGstSource(op.Ctx.DocEntry);
            long readMs = sw.ElapsedMilliseconds;
            br = InvoiceJsonBuilder.Build(op.Source, _s.Cfg, transport);
            Logger.Info("ctx.build", "docEntry", op.Ctx.DocEntry, "sapReadMs", readMs, "totalMs", sw.ElapsedMilliseconds);
            op.Contract = br.Json;
            if (!br.Ok)
            {
                Dialogs.Checks("GST invoice data from SAP", br.Errors.Select(e => new CheckLine { Name = "SAP data", Ok = false, Message = e }).ToList(), "Nothing was sent to GST Portal.");
                op.Finish(); return false;
            }
            return true;
        }

        private static IList<CheckLine> Lines(GstApiResult r)
        {
            var l = new List<CheckLine>();
            foreach (var e in r.Errors) l.Add(new CheckLine { Name = string.IsNullOrEmpty(e.Field) ? e.Code : e.Field, Ok = false, Message = e.Message });
            foreach (var w in r.Warnings) l.Add(new CheckLine { Name = "Note", Ok = true, Warning = true, Message = w.Message });
            if (r.Ok && !r.Errors.Any()) l.Insert(0, new CheckLine { Name = "Validation", Ok = true, Message = r.Message });
            if (!r.Ok && !r.Errors.Any()) l.Add(new CheckLine { Name = r.Status, Ok = false, Message = r.Message });
            return l;
        }

        private void Record(string operation, int docEntry, GstApiResult r)
        {
            try { _s.GstStore.AddHistory(new GstHistory { DocEntry = docEntry, Operation = operation, Status = r.Status, RequestId = r.RequestId, Reference = r.Reference, Message = r.Message, At = DateTime.Now }); }
            catch (Exception ex) { Logger.Warn("ctx.history.failed", "error", ex.Message); }
        }

        /// <summary>Stores the real state in SAP (UDT + invoice UDFs through the DI API), refreshes the cache and the open invoice screen.</summary>
        private void Persist(Op op, GstStatus s, string error = null)
        {
            try
            {
                var rec = _s.NewGateway().ToRecord(op.Ctx.DocEntry, op.Ctx.DocNum, s, op.Source == null ? null : op.Source.Buyer.Name, op.Source == null ? null : op.Source.Buyer.Gstin,
                    op.Source == null ? 0 : op.Source.Lines.Sum(l => l.LineTotalLc), op.Source == null ? 0 : op.Source.Taxes.Sum(t => t.AmountLc), op.Source == null ? (DateTime?)null : op.Source.PostingDate);
                if (error != null) rec.Error = error;
                var old = _s.GstStore.GetGst(op.Ctx.DocEntry);                            // keep stored signed data if this read had none (IRP only returns details for ~2 days)
                if (old != null) { if (string.IsNullOrEmpty(rec.SignedQr)) rec.SignedQr = old.SignedQr; if (string.IsNullOrEmpty(rec.SignedInvoice)) rec.SignedInvoice = old.SignedInvoice; }
                if (rec.EInvStatus == "Generated" && !string.IsNullOrEmpty(rec.SignedQr)) rec.QrPath = QrFile.Write(_s.Cfg, _s.Cfg.SapDatabase, rec.DocEntry, rec.SignedQr);
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

        private static string F(DateTime? d) { return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm:ss") : ""; }

        /// <summary>Server state (read-only refresh) with the SAP-stored signed data as fallback; the callback runs on the UI thread.</summary>
        private void Status(Op op, string what, Action<GstStatus> done)
        {
            Bg(op, what, g => g.Status(op.Ctx.DocEntry, op.Ctx.DocNum, true), s =>
            {
                var old = _s.GstStore.GetGst(op.Ctx.DocEntry);
                if (old != null) { if (string.IsNullOrEmpty(s.SignedQr)) s.SignedQr = old.SignedQr; if (string.IsNullOrEmpty(s.SignedInvoice)) s.SignedInvoice = old.SignedInvoice; }
                _cache.Put(op.Ctx.DocEntry, s);
                done(s);
            });
        }

        private static string SignedText(GstStatus s)
        {
            if (string.IsNullOrEmpty(s.SignedInvoice)) return "";
            var d = Jwt.DecodePayload(s.SignedInvoice);
            return s.SignedInvoice + (d == null ? "" : "\r\n\r\n--- decoded payload (as signed by the IRP) ---\r\n" + d.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        // ---------------------------------------------------------------- history / validation / sync / refresh
        private void ShowHistory(Op op)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SAP invoice DocEntry " + op.Ctx.DocEntry + " (DocNum " + op.Ctx.DocNum + ")  |  configuration: " + _s.Cfg.Name + "  |  environment: " + _s.Cfg.Environment);
            var g = _s.GstStore.GetGst(op.Ctx.DocEntry);
            if (g == null) sb.AppendLine("\nNo GST record yet.");
            else sb.AppendLine("\nE-Invoice: " + g.EInvStatus + "  IRN " + g.Irn + "  Ack " + g.AckNo + " " + g.AckDate + "\nE-Way Bill: " + g.EwbStatus + "  No " + g.EwbNo + "  valid until " + g.EwbValid + "\nLast GST refresh: " + g.LastSync + (string.IsNullOrEmpty(g.Error) ? "" : "\nNote: " + g.Error));
            sb.AppendLine("\nOperations (newest first):");
            try { foreach (var h in _s.GstStore.ListHistory(op.Ctx.DocEntry, 40)) sb.AppendLine(h.At.ToString("yyyy-MM-dd HH:mm:ss") + "  " + h.Operation + "  " + h.Status + "  " + h.Message + "  [" + h.RequestId + "]"); }
            catch (Exception ex) { sb.AppendLine("(history not readable: " + ex.Message + ")"); }
            Dialogs.Info("Integration History", sb.ToString());
            op.Finish();
        }

        private void Validate(Op op)
        {
            BuildResult br;
            if (!Build(op, null, out br)) return;
            Bg(op, "Validating with the GST Portal API", g => g.Validate(op.Ctx.DocEntry, op.Contract), r =>
            {
                Record("Validate", op.Ctx.DocEntry, r);
                Dialogs.Checks("Validate GST invoice - SAP " + op.Ctx.DocNum, Lines(r), "Nothing was sent to the IRP.");
                op.Finish();
            });
        }

        private void Sync(Op op)
        {
            // reconciles uncertain transactions (the server only does so for identical invoice data) and refreshes both statuses
            BuildResult br;
            if (!Build(op, null, out br)) return;
            Bg(op, "Synchronizing GST data", g => g.Status(op.Ctx.DocEntry, op.Ctx.DocNum, true, op.Contract), s =>
            {
                _cache.MarkUncertain(op.Ctx.DocEntry, s.EInvoiceUnknown || s.EwbUnknown);
                Persist(op, s);
                Record("Synchronize", op.Ctx.DocEntry, new GstApiResult { Status = s.EInvoiceStatus + " / " + s.EwbStatus, Message = s.LastMessage });
                Dialogs.Message("Synchronize GST Data", "E-Invoice: " + s.EInvoiceStatus + (s.HasIrn ? "  IRN " + s.Irn : "") + "\nE-Way Bill: " + s.EwbStatus + (s.HasEwb ? "  No " + s.EwbNo : "") + (string.IsNullOrEmpty(s.LastMessage) ? "" : "\n\n" + s.LastMessage));
                op.Finish();
            });
        }

        private void Refresh(Op op, CtxAction a)
        {
            Bg(op, "Refreshing GST status", g => g.Status(op.Ctx.DocEntry, op.Ctx.DocNum, true), s =>
            {
                Persist(op, s);
                if (s.EInvoiceUnknown || s.EwbUnknown)
                {
                    _cache.MarkUncertain(op.Ctx.DocEntry, true);
                    Dialogs.Message("Refresh status", "A previous request has an UNKNOWN outcome. Use 'Synchronize GST Data / Send Request' to reconcile it with the IRP before generating again.\n\n" + s.LastMessage, true);
                }
                else
                {
                    _cache.MarkUncertain(op.Ctx.DocEntry, false);
                    Dialogs.Message("Refresh status", (a == CtxAction.RefreshEwb ? "E-Way Bill: " + s.EwbStatus + (s.HasEwb ? "  No " + s.EwbNo + "  valid until " + F(s.EwbValidUpto) : "")
                                                    : a == CtxAction.RefreshIrn ? "E-Invoice: " + s.EInvoiceStatus + (s.HasIrn ? "  IRN " + s.Irn : "")
                                                    : "E-Invoice: " + s.EInvoiceStatus + (s.HasIrn ? "  IRN " + s.Irn : "") + "\nE-Way Bill: " + s.EwbStatus + (s.HasEwb ? "  No " + s.EwbNo : "")) + "\nNothing was generated by this refresh.");
                }
                op.Finish();
            });
        }

        // ---------------------------------------------------------------- e-Invoice
        private void GenerateEInv(Op op)
        {
            if (_cache.IsUncertain(op.Ctx.DocEntry))
            {
                Dialogs.Message("Generate E-Invoice", "A previous request for this invoice has an UNKNOWN outcome.\nUse 'Synchronize GST Data / Send Request' first; generation stays blocked until it is reconciled.", true);
                op.Finish(); return;
            }
            BuildResult br;
            if (!Build(op, null, out br)) return;
            Bg(op, "Validating with the GST Portal API", g => g.Validate(op.Ctx.DocEntry, op.Contract), v =>
            {
                if (!v.Ok) { Record("Validate", op.Ctx.DocEntry, v); Dialogs.Checks("Generate E-Invoice - cannot continue", Lines(v), "Fix the failed checks, then try again."); op.Finish(); return; }
                string env = _s.Cfg.Environment;
                var subsArr = v.Data["sandbox_substitutions"] as JArray;
                string subs = subsArr != null && subsArr.Count > 0 ? "\n\nSANDBOX: test identities will replace the seller/buyer GSTIN for the sandbox IRP." : "";
                if (!Dialogs.Confirm("Generate E-Invoice", "Generate the e-Invoice (IRN) for SAP invoice " + op.Ctx.DocNum + " (" + op.Contract["document_number"] + ")?\n\nGST Portal will call the " + env.ToUpperInvariant() + " IRP." + (env == "Production" ? "\nThis creates a REAL IRN on the government portal." : "") + subs)) { op.Finish(); return; }
                Bg(op, "Generating e-Invoice", g => g.GenerateEInvoice(op.Ctx.DocEntry, op.Contract), r =>
                {
                    Record("Generate e-Invoice", op.Ctx.DocEntry, r);
                    if (r.IsUnknown) _cache.MarkUncertain(op.Ctx.DocEntry, true);
                    if (r.Ok && !string.IsNullOrEmpty(r.S("irn")))
                    {
                        var st = new GstStatus { SalesInvoice = "SAP " + op.Ctx.DocNum, DocStatus = 1, Irn = r.S("irn"), AckNo = r.S("ack_no"), AckDate = GstGateway.ParseDt(r.S("ack_date")), SignedQr = r.S("signed_qr"), SignedInvoice = r.S("signed_invoice"), EInvoiceStatus = "Generated", EwbStatus = "Not generated" };
                        _cache.MarkUncertain(op.Ctx.DocEntry, false);
                        Persist(op, st);
                        Dialogs.Info("E-Invoice generated", "IRN: " + st.Irn + "\nAck No: " + st.AckNo + "\nAck Date: " + F(st.AckDate) + "\n\nThe IRN was returned by the IRP through GST Portal and is stored in SAP.");
                    }
                    else
                    {
                        if (r.IsUnknown) Persist(op, new GstStatus { SalesInvoice = "SAP " + op.Ctx.DocNum, DocStatus = 1, EInvoiceUnknown = true, EInvoiceStatus = "Unknown", EwbStatus = "Not generated", LastMessage = r.Message }, "UNKNOWN: " + r.Message);
                        Dialogs.Message("Generate E-Invoice", (r.IsUnknown ? "UNKNOWN: " : "NOT generated: ") + r.Describe(), true);
                    }
                    op.Finish();
                });
            });
        }

        private void ViewIrn(Op op)
        {
            Status(op, "Reading IRN details", s =>
            {
                Persist(op, s);
                string text = "SAP Invoice: " + op.Ctx.DocNum + "  (DocEntry " + op.Ctx.DocEntry + ")\n\nIRN: " + s.Irn + "\nIRN Status: " + (s.HasIrn ? "Generated" : (s.IrnCancelled ? "Cancelled" : "Not generated")) +
                    "\nAcknowledgement Number: " + s.AckNo + "\nAcknowledgement Date: " + F(s.AckDate) + "\nCancellation Status: " + (s.IrnCancelled ? "Cancelled" : "Not cancelled");
                Dialogs.Info("IRN - SAP " + op.Ctx.DocNum, text,
                    new Dialogs.Btn { Caption = "Copy IRN", Click = () => { if (!string.IsNullOrEmpty(s.Irn)) System.Windows.Forms.Clipboard.SetText(s.Irn); } },
                    new Dialogs.Btn { Caption = "View QR Code", Click = () => Dialogs.Qr(s) },
                    new Dialogs.Btn { Caption = "Download Signed", Click = () => Dialogs.SaveText("signed-einvoice-" + op.Ctx.DocNum + ".json", SignedText(s)) });
                op.Finish();
            });
        }

        private void ViewAck(Op op)
        {
            Status(op, "Reading acknowledgement", s =>
            {
                var d = Jwt.DecodePayload(s.SignedInvoice);
                var sb = new StringBuilder("Acknowledgement Number: " + s.AckNo + "\nAcknowledgement Date: " + F(s.AckDate) + "\nIRN: " + s.Irn + "\n");
                if (d != null)
                    sb.AppendLine("\nAs signed by the IRP:\nDocument: " + d["DocDtls"]?["Typ"] + " " + d["DocDtls"]?["No"] + " dated " + d["DocDtls"]?["Dt"] + "\nSeller GSTIN: " + d["SellerDtls"]?["Gstin"] + "\nBuyer GSTIN: " + d["BuyerDtls"]?["Gstin"] + "\nTotal invoice value: " + d["ValDtls"]?["TotInvVal"]);
                else sb.AppendLine("\n(The signed document is not available for display.)");
                Dialogs.Info("Acknowledgement - SAP " + op.Ctx.DocNum, sb.ToString());
                op.Finish();
            });
        }

        private void PrintEInv(Op op)
        {
            BuildResult br;
            if (!Build(op, null, out br)) return;
            Status(op, "Preparing e-Invoice print", s =>
            {
                if (!s.HasIrn) { Dialogs.Message("Print", "There is no active IRN to print.", true); op.Finish(); return; }
                string path = Path.Combine(Path.GetTempPath(), "einvoice-" + _s.Cfg.SapDatabase + "-" + op.Ctx.DocNum + ".html");
                File.WriteAllText(path, PrintDocs.EInvoice(op.Contract, s), new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                op.Finish();
            });
        }

        private void CancelEInv(Op op)
        {
            Status(op, "Checking cancellation eligibility", s =>
            {
                if (!s.HasIrn) { Dialogs.Message("Cancel E-Invoice", "There is no active IRN to cancel.", true); op.Finish(); return; }
                if (s.AckDate.HasValue && s.AckDate.Value.AddDays(1) < DateTime.Now) { Dialogs.Message("Cancel E-Invoice", "An e-Invoice can be cancelled only within 24 hours of generation.", true); op.Finish(); return; }
                var vals = Dialogs.Input("Cancel E-Invoice - SAP " + op.Ctx.DocNum, "IRN " + s.Irn + "\nOnly the e-Invoice is cancelled at the IRP. The SAP invoice is not changed or deleted, and nothing happens in GST Portal accounting (there is no GST Portal invoice).",
                    new[] { new Dialogs.Field { Key = "reason", Label = "Cancellation reason", Options = Reasons, Default = Reasons[2] }, new Dialogs.Field { Key = "remark", Label = "Remarks" } }, "Cancel E-Invoice", null);
                if (vals == null) { op.Finish(); return; }
                if (!Dialogs.Confirm("Cancel E-Invoice", "Cancel the e-Invoice of SAP invoice " + op.Ctx.DocNum + " on the " + _s.Cfg.Environment.ToUpperInvariant() + " IRP?\nReason: " + vals["reason"])) { op.Finish(); return; }
                Bg(op, "Cancelling e-Invoice", g => g.CancelEInvoice(op.Ctx.DocEntry, vals["reason"], vals["remark"]), r =>
                {
                    Record("Cancel e-Invoice", op.Ctx.DocEntry, r);
                    if (r.Ok) Persist(op, new GstStatus { SalesInvoice = "SAP " + op.Ctx.DocNum, DocStatus = 1, IrnCancelled = true, EInvoiceStatus = "Cancelled", AckNo = s.AckNo, AckDate = s.AckDate, EwbStatus = "Not generated" });
                    Dialogs.Message("Cancel E-Invoice", (r.Ok ? "Cancelled (confirmed by the IRP): " : r.IsUnknown ? "UNKNOWN: " : "NOT cancelled: ") + r.Describe(), !r.Ok);
                    op.Finish();
                });
            });
        }

        // ---------------------------------------------------------------- e-Way Bill
        private readonly Dictionary<string, DateTime> _validated = new Dictionary<string, DateTime>();
        private static string VKey(int docEntry, JObject contract) { return docEntry + ":" + contract.ToString(Newtonsoft.Json.Formatting.None); }
        private void MarkValidated(int docEntry, JObject contract)
        {
            lock (_validated) { if (_validated.Count > 50) _validated.Clear(); _validated[VKey(docEntry, contract)] = DateTime.Now; }
        }
        private bool WasValidated(int docEntry, JObject contract)
        {
            DateTime t; lock (_validated) return _validated.TryGetValue(VKey(docEntry, contract), out t) && (DateTime.Now - t).TotalMinutes < 10;
        }

        private static decimal Dec(string s) { decimal d; decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d); return d; }


        private void GenerateEwb(Op op, bool validateOnly)
        {
            if (!validateOnly && _cache.IsUncertain(op.Ctx.DocEntry))
            {
                Dialogs.Message("Generate E-Way Bill", "A previous request for this invoice has an UNKNOWN outcome.\nUse 'Synchronize GST Data / Send Request' first; generation stays blocked until it is reconciled.", true);
                op.Finish(); return;
            }
            // The transport details come ONLY from SAP's standard E-Way Bill form of this A/R Invoice (table INV26, DocEntry of the selected invoice).
            // There is no second entry screen: if something is missing, the user is told which SAP field to fill and runs the action again.
            TransportInput input;
            try { input = _s.Source.GetStandardTransport(op.Ctx.DocEntry); }
            catch (Exception ex)
            {
                Logger.Warn("ctx.stdtransport.failed", "error", ex.Message);
                Dialogs.Message((validateOnly ? "Validate" : "Generate") + " E-Way Bill", "The E-Way Bill details of SAP invoice " + op.Ctx.DocNum + " could not be read from SAP: " + ex.Message, true);
                op.Finish(); return;
            }
            var missing = SapReader.MissingTransportFields(input);
            if (missing.Count > 0)
            {
                Dialogs.Message((validateOnly ? "Validate" : "Generate") + " E-Way Bill - SAP " + op.Ctx.DocNum, string.Join("\n\n", missing) + "\n\nSave (Update) the invoice in SAP after entering it, then run this action again.", true);
                op.Finish(); return;
            }
            BuildResult br;
            if (!Build(op, input, out br)) return;
            // The same data was already validated by the dialog's Validate button a moment ago: skip the second server round trip (the server validates again on generate anyway).
            Func<GstGateway, GstApiResult> validateCall = g => !validateOnly && WasValidated(op.Ctx.DocEntry, op.Contract)
                ? new GstApiResult { Ok = true, Status = "Valid", Message = "Validated moments ago (unchanged data)." }
                : g.Validate(op.Ctx.DocEntry, op.Contract);
            Bg(op, "Validating with the GST Portal API", validateCall, v =>
            {
                if (v.Ok) MarkValidated(op.Ctx.DocEntry, op.Contract);
                if (!v.Ok || validateOnly) { Dialogs.Checks((validateOnly ? "Validate" : "Generate") + " E-Way Bill - SAP " + op.Ctx.DocNum, Lines(v), validateOnly ? "Nothing was generated." : "Fix the failed checks and try again."); op.Finish(); return; }
                if (!Dialogs.Confirm("Generate E-Way Bill", "Generate the E-Way Bill for SAP invoice " + op.Ctx.DocNum + "?\nVehicle " + input.VehicleNo + ", " + input.Mode + ", " + input.Distance + " km.\nGST Portal will call the " + _s.Cfg.Environment.ToUpperInvariant() + " e-Way Bill system." + (_s.Cfg.Environment == "Production" ? "\nThis creates a REAL E-Way Bill." : ""))) { op.Finish(); return; }
                Bg(op, "Generating e-Way Bill", g => g.GenerateEWayBill(op.Ctx.DocEntry, op.Contract), r =>
                {
                    Record("Generate e-Way Bill", op.Ctx.DocEntry, r);
                    if (r.IsUnknown) _cache.MarkUncertain(op.Ctx.DocEntry, true);
                    if (r.Ok && !string.IsNullOrEmpty(r.S("ewb_no")))
                    {
                        var old = _cache.Get(op.Ctx.DocEntry);
                        var st = new GstStatus { SalesInvoice = "SAP " + op.Ctx.DocNum, DocStatus = 1, EwbNo = r.S("ewb_no"), EwbDate = GstGateway.ParseDt(r.S("ewb_date")), EwbValidUpto = GstGateway.ParseDt(r.S("ewb_valid_till")), EwbStatus = "Generated",
                            VehicleNo = input.VehicleNo, ModeOfTransport = input.Mode, Distance = input.Distance, TransporterId = input.TransporterId,
                            Irn = old == null ? "" : old.Irn, AckNo = old == null ? "" : old.AckNo, AckDate = old == null ? null : old.AckDate, SignedQr = old == null ? "" : old.SignedQr, SignedInvoice = old == null ? "" : old.SignedInvoice, EInvoiceStatus = old == null ? "Not generated" : old.EInvoiceStatus };
                        _cache.MarkUncertain(op.Ctx.DocEntry, false);
                        Persist(op, st);
                        Dialogs.Info("E-Way Bill generated", "E-Way Bill No: " + st.EwbNo + "\nDate: " + F(st.EwbDate) + "\nValid until: " + F(st.EwbValidUpto) + "\n\nReturned by the e-Way Bill system through GST Portal and stored in SAP.");
                    }
                    else Dialogs.Message("Generate E-Way Bill", (r.IsUnknown ? "UNKNOWN: " : "NOT generated: ") + r.Describe(), true);
                    op.Finish();
                });
            });
        }

        private void ViewEwb(Op op)
        {
            Status(op, "Reading e-Way Bill details", s =>
            {
                Persist(op, s);
                string text = "SAP Invoice: " + op.Ctx.DocNum + "  (DocEntry " + op.Ctx.DocEntry + ")\n\nE-Way Bill Number: " + s.EwbNo + "\nStatus: " + s.EwbStatus + "\nGenerated: " + F(s.EwbDate) + "\nValid until: " + F(s.EwbValidUpto) + "\nVehicle: " + s.VehicleNo;
                Dialogs.Info("E-Way Bill - SAP " + op.Ctx.DocNum, text,
                    new Dialogs.Btn { Caption = "Print", Click = () => PrintEwbPage(op, s, true) }, new Dialogs.Btn { Caption = "Download", Click = () => PrintEwbPage(op, s, false) });
                op.Finish();
            });
        }

        private void PrintEwbPage(Op op, GstStatus s, bool open)
        {
            BuildResult br; var tmp = new Op { Ctx = op.Ctx, Release = () => { } };
            if (!Build(tmp, new TransportInput { VehicleNo = s.VehicleNo, Mode = string.IsNullOrEmpty(s.ModeOfTransport) ? "Road" : s.ModeOfTransport, Distance = s.Distance, TransporterId = s.TransporterId }, out br)) return;
            string html = PrintDocs.EWayBill(tmp.Contract, s);
            if (open) { string path = Path.Combine(Path.GetTempPath(), "ewaybill-" + s.EwbNo + ".html"); File.WriteAllText(path, html, new UTF8Encoding(false)); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            else Dialogs.SaveText("ewaybill-" + s.EwbNo + ".html", html);
        }

        private void PrintEwb(Op op, bool open)
        {
            Status(op, "Preparing e-Way Bill", s =>
            {
                if (!s.HasEwb) { Dialogs.Message("E-Way Bill", "There is no active E-Way Bill.", true); op.Finish(); return; }
                PrintEwbPage(op, s, open);
                op.Finish();
            });
        }

        private void UpdateVehicle(Op op)
        {
            Status(op, "Reading e-Way Bill", s =>
            {
                if (!s.HasEwb) { Dialogs.Message("Update Vehicle", "There is no active E-Way Bill for this invoice.", true); op.Finish(); return; }
                var vals = Dialogs.Input("Update Vehicle - " + s.EwbNo, "The update is sent to the e-Way Bill system through GST Portal.",
                    new[]
                    {
                        new Dialogs.Field { Key = "ewb", Label = "E-Way Bill Number", Default = s.EwbNo, ReadOnly = true }, new Dialogs.Field { Key = "old", Label = "Existing Vehicle Number", Default = s.VehicleNo, ReadOnly = true },
                        new Dialogs.Field { Key = "veh", Label = "New Vehicle Number" }, new Dialogs.Field { Key = "reason", Label = "Update Reason", Options = VehicleReasons, Default = VehicleReasons[0] },
                        new Dialogs.Field { Key = "place", Label = "Place" }, new Dialogs.Field { Key = "state", Label = "State", Options = GstStateNames.Options() }, new Dialogs.Field { Key = "remark", Label = "Remarks" }
                    }, "Update Vehicle",
                    v => { if (v["veh"].Length == 0 || v["place"].Length == 0 || v["state"].Length == 0) { Dialogs.Message("Update Vehicle", "New vehicle number, place and state are required.", true); return false; } return true; });
                if (vals == null) { op.Finish(); return; }
                if (!Dialogs.Confirm("Update Vehicle", "Update the vehicle of E-Way Bill " + s.EwbNo + " to " + vals["veh"].ToUpperInvariant() + "?")) { op.Finish(); return; }
                string state = vals["state"].Substring(0, 2);
                Bg(op, "Updating vehicle", g => g.UpdateVehicle(op.Ctx.DocEntry, vals["veh"], vals["reason"], vals["remark"], vals["place"], state, string.IsNullOrEmpty(s.ModeOfTransport) ? "Road" : s.ModeOfTransport), r =>
                {
                    Record("Update vehicle", op.Ctx.DocEntry, r);
                    if (r.Ok) { s.VehicleNo = vals["veh"].ToUpperInvariant(); var v2 = GstGateway.ParseDt(r.S("ewb_valid_till")); if (v2.HasValue) s.EwbValidUpto = v2; Persist(op, s); }
                    Dialogs.Message("Update Vehicle", (r.Ok ? "Done (confirmed by the portal): " : r.IsUnknown ? "UNKNOWN: " : "NOT updated: ") + r.Describe(), !r.Ok);
                    op.Finish();
                });
            });
        }

        private void UpdateTransporter(Op op)
        {
            Status(op, "Reading e-Way Bill", s =>
            {
                if (!s.HasEwb) { Dialogs.Message("Update Transporter", "There is no active E-Way Bill for this invoice.", true); op.Finish(); return; }
                var vals = Dialogs.Input("Update Transporter - " + s.EwbNo, "The transporter is identified by its GSTIN. The name is informational (the portal takes the registered name).",
                    new[]
                    {
                        new Dialogs.Field { Key = "ewb", Label = "E-Way Bill Number", Default = s.EwbNo, ReadOnly = true }, new Dialogs.Field { Key = "old", Label = "Existing Transporter", Default = (s.TransporterName + " " + s.TransporterId).Trim(), ReadOnly = true },
                        new Dialogs.Field { Key = "tid", Label = "New Transporter GSTIN" }, new Dialogs.Field { Key = "tname", Label = "New Transporter Name (info)" }
                    }, "Update Transporter",
                    v => { if (v["tid"].Length == 0) { Dialogs.Message("Update Transporter", "The new transporter GSTIN is required.", true); return false; } return true; });
                if (vals == null) { op.Finish(); return; }
                if (!Dialogs.Confirm("Update Transporter", "Change the transporter of E-Way Bill " + s.EwbNo + " to " + vals["tid"].ToUpperInvariant() + "?")) { op.Finish(); return; }
                Bg(op, "Updating transporter", g => g.UpdateTransporter(op.Ctx.DocEntry, vals["tid"].ToUpperInvariant()), r =>
                {
                    Record("Update transporter", op.Ctx.DocEntry, r);
                    if (r.Ok) { s.TransporterId = vals["tid"].ToUpperInvariant(); s.TransporterName = vals["tname"]; Persist(op, s); }
                    Dialogs.Message("Update Transporter", (r.Ok ? "Done (confirmed by the portal): " : r.IsUnknown ? "UNKNOWN: " : "NOT updated: ") + r.Describe(), !r.Ok);
                    op.Finish();
                });
            });
        }

        private void CancelEwb(Op op)
        {
            Status(op, "Checking cancellation eligibility", s =>
            {
                if (!s.HasEwb) { Dialogs.Message("Cancel E-Way Bill", "There is no active E-Way Bill for this invoice.", true); op.Finish(); return; }
                if (s.EwbDate.HasValue && s.EwbDate.Value.AddDays(1) < DateTime.Now) { Dialogs.Message("Cancel E-Way Bill", "An E-Way Bill can be cancelled only within 24 hours of generation.", true); op.Finish(); return; }
                var vals = Dialogs.Input("Cancel E-Way Bill - " + s.EwbNo, "E-Way Bill " + s.EwbNo + " of SAP invoice " + op.Ctx.DocNum + ". Only the E-Way Bill is cancelled.",
                    new[] { new Dialogs.Field { Key = "reason", Label = "Cancellation reason", Options = Reasons, Default = Reasons[2] }, new Dialogs.Field { Key = "remark", Label = "Remarks" } }, "Cancel E-Way Bill", null);
                if (vals == null) { op.Finish(); return; }
                if (!Dialogs.Confirm("Cancel E-Way Bill", "Cancel E-Way Bill " + s.EwbNo + " on the " + _s.Cfg.Environment.ToUpperInvariant() + " e-Way Bill system?\nReason: " + vals["reason"])) { op.Finish(); return; }
                Bg(op, "Cancelling e-Way Bill", g => g.CancelEWayBill(op.Ctx.DocEntry, vals["reason"], vals["remark"]), r =>
                {
                    Record("Cancel e-Way Bill", op.Ctx.DocEntry, r);
                    if (r.Ok) { s.EwbCancelled = true; s.EwbStatus = "Cancelled"; Persist(op, s); }
                    Dialogs.Message("Cancel E-Way Bill", (r.Ok ? "Cancelled (confirmed by the portal): " : r.IsUnknown ? "UNKNOWN: " : "NOT cancelled: ") + r.Describe(), !r.Ok);
                    op.Finish();
                });
            });
        }
    }
}
