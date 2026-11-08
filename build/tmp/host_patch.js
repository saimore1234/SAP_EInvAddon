const fs = require('fs');
const p = 'C:/EInvGstAddon/src/EInvGstAddon.UI/Host.cs';
let s = fs.readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
function rep(a, b) { if (!s.includes(a)) { console.log('MISSING: ' + a.slice(0, 80)); return; } s = s.replace(a, b); }

rep('public void Open(string type, Func<FormBase> factory)', 'public FormBase Open(string type, Func<FormBase> factory)');
rep('if (existing != null) { try { existing.Show(); return; } catch { _open.Remove(existing.Uid); } }', 'if (existing != null) { try { existing.Show(); return existing; } catch { _open.Remove(existing.Uid); } }');
rep('            _open[created.Uid] = created;\n            created.Show();\n        }', '            _open[created.Uid] = created;\n            created.Show();\n            return created;\n        }');

rep('new[] { "EINV_M_HIST", "Integration History" }', 'new[] { "EINV_M_HIST", "Integration History" },\n            new[] { "EINV_M_EINV", "Generate E-Invoice" }, new[] { "EINV_M_EWB", "Generate E-Way Bill" }, new[] { "EINV_M_REGI", "E-Invoice Register" },\n            new[] { "EINV_M_REGW", "E-Way Bill Register" }, new[] { "EINV_M_GST", "GST Configuration" }, new[] { "EINV_M_TAX", "Tax & Account Mapping" }');

rep('            _s.Cfg = ConfigStore.Load();\n', '');
rep('            _s.Di = new DiSession(_s.Sap.GetDiCompany(), new UiThread());\n            _s.Store = new DiStateStore(_s.Di);',
    '            _s.Di = new DiSession(_s.Sap.GetDiCompany(), new UiThread());\n' +
    '            // the active configuration is chosen by the SAP company database that is open right now\n' +
    '            var cfg = ConfigStore.FindFor(_s.Di.DatabaseName);\n' +
    '            _s.HasConfig = cfg != null;\n' +
    '            _s.Cfg = cfg ?? new AppConfig { Id = AppConfig.NewId(), SapDatabase = _s.Di.DatabaseName, Active = false, AutoSync = false, Name = "(not configured)" };\n' +
    '            _s.Store = new DiStateStore(_s.Di, _s.Cfg.StoredConfigId);');
rep('            if (_s.SchemaReady && string.Equals(_s.Sap.DatabaseName, _s.Cfg.SapDatabase, StringComparison.OrdinalIgnoreCase))',
    '            if (!_s.HasConfig) _s.Status("E-Invoice & ERPNext: no configuration for company database \'" + _s.Di.DatabaseName + "\'. Open ERPNext Connection Settings to create one.", true);\n' +
    '            if (_s.HasConfig && _s.SchemaReady && string.Equals(_s.Sap.DatabaseName, _s.Cfg.SapDatabase, StringComparison.OrdinalIgnoreCase))');

rep('                    case "EINV_M_DASH": _s.Router.Open(DashboardForm.Type, () => new DashboardForm(_s)); break;',
    '                    case "EINV_M_SET": _s.Router.Open(SettingsForm.Type, () => new SettingsForm(_s)); break;\n' +
    '                    default:\n' +
    '                        if (!_s.HasConfig) { _s.Alert("There is no active ERPNext configuration for the open SAP company (" + _s.Di.DatabaseName + ").\\nOpen ERPNext Connection Settings first."); break; }\n' +
    '                        OpenConfigured(uid);\n' +
    '                        break;\n' +
    '                }\n' +
    '            }\n' +
    '            catch (Exception ex) { Logger.Error("menu.failed", ex, "uid", uid); _s.Alert("Could not open the screen: " + ex.Message); }\n' +
    '        }\n\n' +
    '        private void OpenConfigured(string uid)\n' +
    '        {\n' +
    '            {\n' +
    '                switch (uid)\n' +
    '                {\n' +
    '                    case "EINV_M_DASH": _s.Router.Open(DashboardForm.Type, () => new DashboardForm(_s)); break;\n' +
    '                    case "EINV_M_EINV": _s.Router.Open(EInvoiceForm.Type, () => new EInvoiceForm(_s)); break;\n' +
    '                    case "EINV_M_EWB": _s.Router.Open(EWayBillForm.Type, () => new EWayBillForm(_s)); break;\n' +
    '                    case "EINV_M_REGI": _s.Router.Open(RegisterForm.TypeOf(false), () => new RegisterForm(_s, false)); break;\n' +
    '                    case "EINV_M_REGW": _s.Router.Open(RegisterForm.TypeOf(true), () => new RegisterForm(_s, true)); break;\n' +
    '                    case "EINV_M_GST": _s.Router.Open(GstConfigForm.Type, () => new GstConfigForm(_s)); break;\n' +
    '                    case "EINV_M_TAX": _s.Router.Open(TaxMappingForm.Type, () => new TaxMappingForm(_s)); break;');
// remove the original SETTINGS case (now handled above) and fix the tail of the old try/catch block
rep('                    case "EINV_M_SET": _s.Router.Open(SettingsForm.Type, () => new SettingsForm(_s)); break;\n                }\n            }\n            catch (Exception ex) { Logger.Error("menu.failed", ex, "uid", uid); _s.Alert("Could not open the screen: " + ex.Message); }\n        }',
    '                }\n            }\n        }');
fs.writeFileSync(p, s);
console.log('patched');
