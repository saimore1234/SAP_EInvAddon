using System;
using System.Runtime.InteropServices;
using EInvGstAddon.Common;

namespace EInvGstAddon.UI
{
    /// <summary>Connects to the running SAP B1 client with the connection string passed by the add-on framework (never stored).</summary>
    public sealed class SapConnection
    {
        private SAPbouiCOM.SboGuiApi _gui;
        public SAPbouiCOM.Application App { get; private set; }

        public void Connect(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("SAP B1 connection string was not supplied.");
            _gui = new SAPbouiCOM.SboGuiApi();
            _gui.Connect(connectionString);
            App = _gui.GetApplication(-1);
            Logger.Info("ui.connected", "company", Safe(() => App.Company.Name), "database", Safe(() => App.Company.DatabaseName), "user", Safe(() => App.Company.UserName));
        }

        /// <summary>DI API company object that shares this client session.</summary>
        public object GetDiCompany() { return App.Company.GetDICompany(); }

        public string DatabaseName { get { return Safe(() => App.Company.DatabaseName); } }
        public string UserName { get { return Safe(() => App.Company.UserName); } }

        private static string Safe(Func<string> f) { try { return f(); } catch { return "?"; } }

        public void Status(string text, bool error)
        {
            try { App.StatusBar.SetText(text, SAPbouiCOM.BoMessageTime.bmt_Short, error ? SAPbouiCOM.BoStatusBarMessageType.smt_Error : SAPbouiCOM.BoStatusBarMessageType.smt_Success); }
            catch (Exception ex) { Logger.Warn("ui.statusbar.failed", "error", ex.Message); }
        }

        public void Release()
        {
            try { if (App != null && Marshal.IsComObject(App)) Marshal.ReleaseComObject(App); if (_gui != null && Marshal.IsComObject(_gui)) Marshal.ReleaseComObject(_gui); }
            catch { }
            App = null; _gui = null;
        }
    }
}
