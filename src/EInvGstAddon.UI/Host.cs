using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using EInvGstAddon.Database;
using EInvGstAddon.ERPNext;

namespace EInvGstAddon.UI
{
    public sealed class FormRouter
    {
        private readonly AddonServices _svc;
        private readonly Dictionary<string, FormBase> _open = new Dictionary<string, FormBase>();
        public FormRouter(AddonServices svc) { _svc = svc; }

        public FormBase Open(string type, Func<FormBase> factory)
        {
            var existing = _open.Values.FirstOrDefault(f => f.Form != null && f.Form.TypeEx == type);
            if (existing != null) { try { existing.Show(); return existing; } catch { _open.Remove(existing.Uid); } }
            var created = factory();
            _open[created.Uid] = created;
            created.Show();
            return created;
        }

        public void OnItemEvent(string formUid, SAPbouiCOM.ItemEvent e, out bool bubble)
        {
            bubble = true;
            FormBase f;
            if (!_open.TryGetValue(formUid, out f)) return;
            if (e.EventType == SAPbouiCOM.BoEventTypes.et_FORM_UNLOAD && !e.BeforeAction) { _open.Remove(formUid); try { f.OnClosed(); } catch { } return; }
            try { f.OnItemEvent(e, out bubble); }
            catch (Exception ex) { Logger.Error("form.event.failed", ex, "form", formUid, "item", e.ItemUID); bubble = true; }
        }

        public static void CloseStale(SAPbouiCOM.Application app)
        {
            try
            {
                for (int i = app.Forms.Count - 1; i >= 0; i--)
                {
                    var f = app.Forms.Item(i);
                    if (f.TypeEx != null && f.TypeEx.StartsWith("EINV_", StringComparison.Ordinal)) { try { f.Close(); } catch { } }
                }
            }
            catch (Exception ex) { Logger.Warn("form.stale.scan.failed", "error", ex.Message); }
        }

        public void CloseAll() { foreach (var f in _open.Values.ToList()) { try { f.Form.Close(); } catch { } } _open.Clear(); }
    }

    public sealed class MenuManager
    {
        public const string Root = "EINV_ROOT";
        private static readonly string[][] Items =
        {
            new[] { "EINV_M_DASH", "Dashboard" }, new[] { "EINV_M_SYNC", "Invoice Synchronization" }, new[] { "EINV_M_PEND", "Pending Synchronization" },
            new[] { "EINV_M_FAIL", "Failed Synchronization" }, new[] { "EINV_M_CUST", "Customer Mapping" }, new[] { "EINV_M_ITEM", "Item Mapping" },
            new[] { "EINV_M_WHS", "Warehouse Mapping" }, new[] { "EINV_M_SET", "ERPNext Connection Settings" }, new[] { "EINV_M_LOGS", "Sync Logs" },
            new[] { "EINV_M_HIST", "Integration History" },
            new[] { "EINV_M_EINV", "Generate E-Invoice" }, new[] { "EINV_M_EWB", "Generate E-Way Bill" }, new[] { "EINV_M_REGI", "E-Invoice Register" },
            new[] { "EINV_M_REGW", "E-Way Bill Register" }, new[] { "EINV_M_GST", "GST Configuration" }, new[] { "EINV_M_TAX", "Tax & Account Mapping" }
        };
        private readonly SAPbouiCOM.Application _app;
        public MenuManager(SAPbouiCOM.Application app) { _app = app; }

        public void Create()
        {
            Remove();
            var modules = _app.Menus.Item("43520");
            var root = (SAPbouiCOM.MenuCreationParams)_app.CreateObject(SAPbouiCOM.BoCreatableObjectType.cot_MenuCreationParams);
            root.Type = SAPbouiCOM.BoMenuType.mt_POPUP; root.UniqueID = Root; root.String = "E-Invoice & ERPNext Integration"; root.Enabled = true; root.Position = modules.SubMenus.Count;
            modules.SubMenus.AddEx(root);
            var subs = _app.Menus.Item(Root).SubMenus;
            for (int i = 0; i < Items.Length; i++)
            {
                var p = (SAPbouiCOM.MenuCreationParams)_app.CreateObject(SAPbouiCOM.BoCreatableObjectType.cot_MenuCreationParams);
                p.Type = SAPbouiCOM.BoMenuType.mt_STRING; p.UniqueID = Items[i][0]; p.String = Items[i][1]; p.Enabled = true; p.Position = i;
                subs.AddEx(p);
            }
        }

        public void Remove()
        {
            for (int i = Items.Length - 1; i >= -1; i--)
            {
                string uid = i >= 0 ? Items[i][0] : Root;
                try { if (_app.Menus.Exists(uid)) _app.Menus.RemoveEx(uid); } catch (Exception ex) { Logger.Warn("menu.remove.failed", "uid", uid, "error", ex.Message); }
            }
        }
    }

    /// <summary>Wires SAP UI API events to the add-on and runs the message loop.</summary>
    public sealed class AddonHost
    {
        private readonly AddonServices _s = new AddonServices();
        private MenuManager _menu;
        private Coordinator _coord;
        private Timer _timer;
        private static readonly Regex DocEntryRx = new Regex(@"<DocEntry>\s*(\d+)\s*</DocEntry>", RegexOptions.IgnoreCase);

        public void Run(string connectionString)
        {
            UiThread.Capture();
            _s.Sap = new SapConnection();
            _s.Sap.Connect(connectionString);
            SapReaderSetup();
            FormRouter.CloseStale(_s.App);
            _s.Router = new FormRouter(_s);
            _coord = new Coordinator(_s);
            _s.Coordinator = _coord;

            try
            {
                var installer = new SchemaInstaller(_s.Di);
                _s.SchemaReady = installer.IsInstalled();
                if (!_s.SchemaReady) _s.Status("E-Invoice & ERPNext: SAP objects are missing. Open ERPNext Connection Settings and click 'Create SAP UDF/UDT'.", true);
            }
            catch (Exception ex) { Logger.Error("schema.check.failed", ex); }

            _s.Queue = new SyncQueue(() => _s.Engine(), (d, o) => _coord.OnResult(d, o));
            _menu = new MenuManager(_s.App);
            _menu.Create();
            _s.App.AppEvent += OnAppEvent;
            _s.App.MenuEvent += OnMenuEvent;
            _s.App.ItemEvent += OnItemEvent;
            _s.App.FormDataEvent += OnFormDataEvent;

            if (!_s.HasConfig) _s.Status("E-Invoice & ERPNext: no configuration for company database '" + _s.Di.DatabaseName + "'. Open ERPNext Connection Settings to create one.", true);
            if (_s.HasConfig && _s.SchemaReady && string.Equals(_s.Sap.DatabaseName, _s.Cfg.SapDatabase, StringComparison.OrdinalIgnoreCase))
            {
                _coord.StartupScan();
                _coord.Tick();
            }
            _timer = new Timer { Interval = 30000 };
            _timer.Tick += (a, b) => _coord.Tick();
            _timer.Start();
            _s.Status("E-Invoice & ERPNext Integration add-on started (" + _s.Sap.DatabaseName + ").", false);
            Application.Run();
        }

        private void SapReaderSetup()
        {
            _s.Di = new DiSession(_s.Sap.GetDiCompany(), new UiThread());
            // the active configuration is chosen by the SAP company database that is open right now
            var cfg = ConfigStore.FindFor(_s.Di.DatabaseName);
            _s.HasConfig = cfg != null;
            _s.Cfg = cfg ?? new AppConfig { Id = AppConfig.NewId(), SapDatabase = _s.Di.DatabaseName, Active = false, AutoSync = false, Name = "(not configured)" };
            _s.Store = new DiStateStore(_s.Di, _s.Cfg.StoredConfigId);
            _s.Source = new SapReader(new DiRunner(_s.Di), _s.Di.DatabaseName);
        }

        private void OnAppEvent(SAPbouiCOM.BoAppEventTypes t)
        {
            switch (t)
            {
                case SAPbouiCOM.BoAppEventTypes.aet_ShutDown:
                case SAPbouiCOM.BoAppEventTypes.aet_ServerTerminition:
                case SAPbouiCOM.BoAppEventTypes.aet_CompanyChanged:
                    Logger.Info("app.event", "type", t);
                    Shutdown();
                    break;
            }
        }

        private void OnMenuEvent(ref SAPbouiCOM.MenuEvent e, out bool bubble)
        {
            bubble = true;
            if (e.BeforeAction) return;
            string uid = e.MenuUID;
            if (uid == null || !uid.StartsWith("EINV_M_", StringComparison.Ordinal)) return;
            try
            {
                switch (uid)
                {
                    case "EINV_M_SET": _s.Router.Open(SettingsForm.Type, () => new SettingsForm(_s)); break;
                    default:
                        if (!_s.HasConfig) { _s.Alert("There is no active ERPNext configuration for the open SAP company (" + _s.Di.DatabaseName + ").\nOpen ERPNext Connection Settings first."); break; }
                        OpenConfigured(uid);
                        break;
                }
            }
            catch (Exception ex) { Logger.Error("menu.failed", ex, "uid", uid); _s.Alert("Could not open the screen: " + ex.Message); }
        }

        private void OpenConfigured(string uid)
        {
            {
                switch (uid)
                {
                    case "EINV_M_DASH": _s.Router.Open(DashboardForm.Type, () => new DashboardForm(_s)); break;
                    case "EINV_M_EINV": _s.Router.Open(EInvoiceForm.Type, () => new EInvoiceForm(_s)); break;
                    case "EINV_M_EWB": _s.Router.Open(EWayBillForm.Type, () => new EWayBillForm(_s)); break;
                    case "EINV_M_REGI": _s.Router.Open(RegisterForm.TypeOf(false), () => new RegisterForm(_s, false)); break;
                    case "EINV_M_REGW": _s.Router.Open(RegisterForm.TypeOf(true), () => new RegisterForm(_s, true)); break;
                    case "EINV_M_GST": _s.Router.Open(GstConfigForm.Type, () => new GstConfigForm(_s)); break;
                    case "EINV_M_TAX": _s.Router.Open(TaxMappingForm.Type, () => new TaxMappingForm(_s)); break;
                    case "EINV_M_SYNC": OpenList(ListMode.All); break;
                    case "EINV_M_PEND": OpenList(ListMode.Pending); break;
                    case "EINV_M_FAIL": OpenList(ListMode.Failed); break;
                    case "EINV_M_LOGS": OpenList(ListMode.Logs); break;
                    case "EINV_M_HIST": OpenList(ListMode.History); break;
                    case "EINV_M_CUST": OpenMap(MapKind.Customer); break;
                    case "EINV_M_ITEM": OpenMap(MapKind.Item); break;
                    case "EINV_M_WHS": OpenMap(MapKind.Warehouse); break;
                }
            }
        }

        private void OpenList(ListMode m) { _s.Router.Open(SyncListForm.TypeOf(m), () => new SyncListForm(_s, m)); }
        private void OpenMap(MapKind k) { _s.Router.Open(MappingForm.TypeOf(k), () => new MappingForm(_s, k)); }

        private void OnItemEvent(string formUid, ref SAPbouiCOM.ItemEvent e, out bool bubble) { bubble = true; _s.Router.OnItemEvent(formUid, e, out bubble); }

        /// <summary>
        /// Primary trigger: A/R Invoice (object 13) added. We act only on the AFTER event with ActionSuccess = true,
        /// i.e. the document is committed in SAP; the heavy work is posted off this callback.
        /// </summary>
        private void OnFormDataEvent(ref SAPbouiCOM.BusinessObjectInfo info, out bool bubble)
        {
            bubble = true;
            try
            {
                if (info.BeforeAction || !info.ActionSuccess) return;
                if (info.EventType != SAPbouiCOM.BoEventTypes.et_FORM_DATA_ADD || info.Type != Constants.ArInvoiceObjType) return;
                var m = DocEntryRx.Match(info.ObjectKey ?? "");
                if (!m.Success) { Logger.Warn("invoice.event.nokey", "key", info.ObjectKey); return; }
                _coord.OnInvoiceAdded(int.Parse(m.Groups[1].Value));
            }
            catch (Exception ex) { Logger.Error("formdata.failed", ex); }
        }

        public void Shutdown()
        {
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_s.Queue != null) _s.Queue.Dispose(); } catch { }
            try { if (_s.Router != null) _s.Router.CloseAll(); } catch { }
            try { if (_menu != null) _menu.Remove(); } catch { }
            try { _s.Sap.Release(); } catch { }
            Application.ExitThread();
            new System.Threading.Thread(() => { System.Threading.Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start();   // never leave a stale process holding the single-instance mutex
        }
    }
}
