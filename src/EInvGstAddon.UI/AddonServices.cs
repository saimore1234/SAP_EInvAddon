using System;
using System.Threading;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.Database;
using EInvGstAddon.ERPNext;

namespace EInvGstAddon.UI
{
    /// <summary>
    /// Everything the screens and event handlers share. Created once at start-up on the main thread.
    /// There is no invoice-synchronisation queue any more: nothing is created in ERPNext for a SAP invoice.
    /// </summary>
    public sealed class AddonServices
    {
        public SapConnection Sap;
        public AppConfig Cfg;
        public DiSession Di;
        public DiStateStore Store;
        public SapReader Source;
        public FormRouter Router;
        public InvoiceContextMenu InvoiceMenu;
        public bool SchemaReady;
        /// <summary>false when no active configuration exists for the open SAP company database (only the configuration screens work).</summary>
        public bool HasConfig;

        public IGstStore GstStore { get { return Store; } }
        public SAPbouiCOM.Application App { get { return Sap.App; } }

        public ErpClient NewClient() { return new ErpClient(Cfg); }
        public GstGateway NewGateway() { return new GstGateway(NewClient()); }

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
}
