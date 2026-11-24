using System;
using System.Collections.Generic;
using System.Linq;

namespace EInvGstAddon.Core
{
    // ====================================================================================================
    // Right-click menu of the A/R Invoice form. Pure logic (no SAP types) so it can be unit-tested; the SAP UI API adapter lives in the UI project.
    // ====================================================================================================

    public enum CtxAction
    {
        // GST Portal / GST API
        ValidateGst, SyncGst, History, RefreshGst,
        // E-Invoice
        ValidateEInv, GenerateEInv, ViewIrn, ViewAck, ViewQr, DownloadSigned, PrintEInv, RefreshIrn, CancelEInv,
        // E-Way Bill
        ValidateEwb, GenerateEwb, ViewEwb, UpdateVehicle, UpdateTransporter, DownloadEwb, PrintEwb, RefreshEwb, CancelEwb
    }

    public sealed class CtxItem
    {
        public CtxAction Action; public string Id, Caption, Group;
    }

    public static class CtxMenuDefinition
    {
        public const string RootId = "EINV_CTX";
        public const string RootCaption = "E-Invoice & E-Way Bill";
        public const string IdPrefix = "EINV_C_";
        public const string ParentMenuId = "1280";           // SAP "Data" menu: its sub-menus are what the right-click menu shows

        public static readonly string[] Groups = { "GST Portal", "E-Invoice", "E-Way Bill" };

        public static string GroupId(string group) { return RootId + "_" + group.Replace(" ", "").Replace("-", "").ToUpperInvariant(); }

        public static readonly List<CtxItem> Items = new List<CtxItem>
        {
            I(CtxAction.ValidateGst, "Validate GST Invoice", "GST Portal"), I(CtxAction.SyncGst, "Synchronize GST Data / Send Request", "GST Portal"),
            I(CtxAction.History, "View Integration History", "GST Portal"), I(CtxAction.RefreshGst, "Refresh GST Status", "GST Portal"),
            I(CtxAction.ValidateEInv, "Validate E-Invoice", "E-Invoice"), I(CtxAction.GenerateEInv, "Generate E-Invoice", "E-Invoice"),
            I(CtxAction.ViewIrn, "View IRN", "E-Invoice"), I(CtxAction.ViewAck, "View Acknowledgement", "E-Invoice"), I(CtxAction.ViewQr, "View QR Code", "E-Invoice"),
            I(CtxAction.DownloadSigned, "Download Signed Data", "E-Invoice"), I(CtxAction.PrintEInv, "Print", "E-Invoice"),
            I(CtxAction.RefreshIrn, "Refresh IRN Status", "E-Invoice"), I(CtxAction.CancelEInv, "Cancel E-Invoice", "E-Invoice"),
            I(CtxAction.ValidateEwb, "Validate E-Way Bill", "E-Way Bill"), I(CtxAction.GenerateEwb, "Generate E-Way Bill", "E-Way Bill"),
            I(CtxAction.ViewEwb, "View EWB Details", "E-Way Bill"), I(CtxAction.UpdateVehicle, "Update Vehicle", "E-Way Bill"),
            I(CtxAction.UpdateTransporter, "Update Transporter", "E-Way Bill"), I(CtxAction.DownloadEwb, "Download", "E-Way Bill"),
            I(CtxAction.PrintEwb, "Print", "E-Way Bill"), I(CtxAction.RefreshEwb, "Refresh EWB Status", "E-Way Bill"), I(CtxAction.CancelEwb, "Cancel EWB", "E-Way Bill")
        };

        private static CtxItem I(CtxAction a, string caption, string group) { return new CtxItem { Action = a, Caption = caption, Group = group, Id = IdPrefix + a.ToString().ToUpperInvariant() }; }

        public static bool TryParse(string menuUid, out CtxAction action)
        {
            var it = Items.FirstOrDefault(x => x.Id == menuUid);
            action = it == null ? default(CtxAction) : it.Action;
            return it != null;
        }
    }

    /// <summary>Which items are enabled for the current invoice (all other items stay visible but greyed out).</summary>
    public sealed class CtxState
    {
        public readonly HashSet<CtxAction> Enabled = new HashSet<CtxAction>();
        public string Note = "";
        public bool Has(CtxAction a) { return Enabled.Contains(a); }
    }

    public static class CtxStateCalculator
    {
        /// <summary>
        /// Enabled/disabled logic from the LAST KNOWN GST state (read from the ERPNext GST API in the background). Every action re-reads the server
        /// state and re-validates before it does anything, so a stale menu can never cause a wrong GST action. There is no ERPNext invoice to "submit":
        /// the SAP invoice is the only source document.
        /// </summary>
        public static CtxState Compute(GstStatus st, bool configured, bool eInvoiceEnabled, bool eWayBillEnabled, DateTime now)
        {
            var s = new CtxState();
            s.Enabled.Add(CtxAction.History);
            if (!configured) { s.Note = "No active GST configuration for this SAP company."; return s; }
            s.Enabled.Add(CtxAction.ValidateGst); s.Enabled.Add(CtxAction.SyncGst); s.Enabled.Add(CtxAction.RefreshGst);
            s.Enabled.Add(CtxAction.RefreshIrn); s.Enabled.Add(CtxAction.RefreshEwb);              // read-only status reads
            if (st == null) { s.Note = "GST status not read yet; reading in the background (right-click again)."; return s; }

            // ---- e-Invoice
            if (eInvoiceEnabled)
            {
                if (!st.HasIrn && !st.IrnCancelled) { s.Enabled.Add(CtxAction.ValidateEInv); if (!st.EInvoiceUnknown) s.Enabled.Add(CtxAction.GenerateEInv); }
                else if (st.HasIrn) s.Enabled.Add(CtxAction.ValidateEInv);
            }
            if (st.HasIrn)
            {
                s.Enabled.Add(CtxAction.ViewIrn); s.Enabled.Add(CtxAction.ViewAck); s.Enabled.Add(CtxAction.ViewQr); s.Enabled.Add(CtxAction.DownloadSigned); s.Enabled.Add(CtxAction.PrintEInv);
                if (eInvoiceEnabled && st.AckDate.HasValue && st.AckDate.Value.AddDays(1) >= now) s.Enabled.Add(CtxAction.CancelEInv);
            }
            else if (st.IrnCancelled) { s.Enabled.Add(CtxAction.ViewIrn); s.Enabled.Add(CtxAction.ViewAck); }

            // ---- e-Way Bill
            if (eWayBillEnabled && !st.HasEwb && !st.EwbCancelled)
            {
                s.Enabled.Add(CtxAction.ValidateEwb); if (!st.EwbUnknown) s.Enabled.Add(CtxAction.GenerateEwb);
            }
            if (st.HasEwb)
            {
                s.Enabled.Add(CtxAction.ViewEwb); s.Enabled.Add(CtxAction.DownloadEwb); s.Enabled.Add(CtxAction.PrintEwb);
                bool valid = !st.EwbValidUpto.HasValue || st.EwbValidUpto.Value >= now;
                if (eWayBillEnabled && valid) { s.Enabled.Add(CtxAction.UpdateVehicle); s.Enabled.Add(CtxAction.UpdateTransporter); }
                if (eWayBillEnabled && st.EwbDate.HasValue && st.EwbDate.Value.AddDays(1) >= now) s.Enabled.Add(CtxAction.CancelEwb);
            }
            else if (st.EwbCancelled) s.Enabled.Add(CtxAction.ViewEwb);
            if (!eInvoiceEnabled && !eWayBillEnabled) s.Note = "GST is not enabled for this configuration (GST Configuration screen).";
            else if (st.EInvoiceUnknown || st.EwbUnknown) s.Note = "A previous request has an UNKNOWN outcome: use Synchronize GST Data to reconcile before generating again.";
            return s;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Menu construction, independent of the SAP API
    // ----------------------------------------------------------------------------------------------------
    public interface IMenuHost
    {
        bool Exists(string id);
        void AddPopup(string parentId, string id, string caption);
        void AddItem(string parentId, string id, string caption, bool enabled);
        void Remove(string id);
    }

    public sealed class CtxMenuBuilder
    {
        private readonly IMenuHost _host;
        public CtxMenuBuilder(IMenuHost host) { _host = host; }

        /// <summary>Idempotent: any previous copy is removed first, so repeated right-clicks never leave duplicate entries.</summary>
        public void Show(CtxState state)
        {
            Hide();
            _host.AddPopup(CtxMenuDefinition.ParentMenuId, CtxMenuDefinition.RootId, CtxMenuDefinition.RootCaption);
            foreach (var g in CtxMenuDefinition.Groups) _host.AddPopup(CtxMenuDefinition.RootId, CtxMenuDefinition.GroupId(g), g);
            foreach (var it in CtxMenuDefinition.Items) _host.AddItem(CtxMenuDefinition.GroupId(it.Group), it.Id, it.Caption, state.Has(it.Action));
        }

        public void Hide()
        {
            foreach (var it in CtxMenuDefinition.Items) if (_host.Exists(it.Id)) _host.Remove(it.Id);
            foreach (var g in CtxMenuDefinition.Groups) if (_host.Exists(CtxMenuDefinition.GroupId(g))) _host.Remove(CtxMenuDefinition.GroupId(g));
            if (_host.Exists(CtxMenuDefinition.RootId)) _host.Remove(CtxMenuDefinition.RootId);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Which invoice is the menu acting on?
    // ----------------------------------------------------------------------------------------------------
    public sealed class FormSnapshot
    {
        public string FormUid, FormType, ObjType, Mode, Database;
        public long DocEntry;
        public int DocNum;
    }

    public sealed class InvoiceContext
    {
        public string FormUid, Database;
        public int DocEntry, DocNum;
    }

    public static class InvoiceContextResolver
    {
        public const string ArInvoiceFormType = "133";
        public const string ArInvoiceObjType = "13";

        /// <summary>Returns a context only for a saved A/R Invoice of the configured SAP company; otherwise null with the reason.</summary>
        public static InvoiceContext Resolve(FormSnapshot f, string configuredDatabase, bool hasConfig, out string reason)
        {
            reason = null;
            if (f == null) { reason = "No active form."; return null; }
            if (f.FormType != ArInvoiceFormType || (f.ObjType != null && f.ObjType != ArInvoiceObjType)) { reason = "Not an A/R Invoice form."; return null; }
            if (!hasConfig) { reason = "No active GST Portal configuration for this SAP company."; return null; }
            if (!string.Equals(f.Database, configuredDatabase, StringComparison.OrdinalIgnoreCase)) { reason = "This SAP company is not the configured one."; return null; }
            if (f.DocEntry <= 0 || f.DocEntry > int.MaxValue) { reason = "The invoice has not been saved yet."; return null; }       // DocEntry identifies the document, never DocNum
            if (!string.Equals(f.Mode, "OK", StringComparison.OrdinalIgnoreCase)) { reason = "The invoice is not in view mode."; return null; }
            return new InvoiceContext { FormUid = f.FormUid, Database = f.Database, DocEntry = (int)f.DocEntry, DocNum = f.DocNum };
        }

        /// <summary>The action may only run if the document in front of the user is still the one the menu was opened on.</summary>
        public static bool IsSameDocument(InvoiceContext saved, FormSnapshot active, out string reason)
        {
            reason = null;
            if (saved == null || active == null) { reason = "The menu context is no longer available."; return false; }
            if (saved.FormUid != active.FormUid || saved.DocEntry != active.DocEntry)
            { reason = "The active document changed after the menu was opened. Nothing was done."; return false; }
            return true;
        }
    }

    /// <summary>Prevents two actions on the same invoice at once and blocks re-entry while an action is starting.</summary>
    public sealed class ActionGate
    {
        private readonly HashSet<int> _busy = new HashSet<int>();
        public bool TryEnter(int docEntry) { lock (_busy) return _busy.Add(docEntry); }
        public void Leave(int docEntry) { lock (_busy) _busy.Remove(docEntry); }
        public bool IsBusy(int docEntry) { lock (_busy) return _busy.Contains(docEntry); }
    }

    /// <summary>Remembers the last ERPNext status read per invoice (background refresh) so the menu can be drawn instantly.</summary>
    public sealed class StatusCache
    {
        private sealed class E { public GstStatus S; public DateTime At; public bool Uncertain; }
        private readonly Dictionary<int, E> _d = new Dictionary<int, E>();
        private readonly TimeSpan _throttle;
        public StatusCache(TimeSpan throttle) { _throttle = throttle; }

        public GstStatus Get(int docEntry) { lock (_d) { E e; return _d.TryGetValue(docEntry, out e) ? e.S : null; } }
        public void Put(int docEntry, GstStatus s) { lock (_d) { E e; if (!_d.TryGetValue(docEntry, out e)) _d[docEntry] = e = new E(); e.S = s; e.At = DateTime.Now; } }
        public bool NeedsRefresh(int docEntry, DateTime now) { lock (_d) { E e; return !_d.TryGetValue(docEntry, out e) || now - e.At > _throttle; } }
        public void Invalidate(int docEntry) { lock (_d) _d.Remove(docEntry); }
        public void MarkUncertain(int docEntry, bool v) { lock (_d) { E e; if (!_d.TryGetValue(docEntry, out e)) _d[docEntry] = e = new E { At = DateTime.MinValue }; e.Uncertain = v; } }
        public bool IsUncertain(int docEntry) { lock (_d) { E e; return _d.TryGetValue(docEntry, out e) && e.Uncertain; } }
    }
}
