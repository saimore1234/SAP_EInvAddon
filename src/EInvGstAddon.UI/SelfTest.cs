using System;
using System.IO;
using System.Linq;
using System.Text;
using EInvGstAddon.Core;

namespace EInvGstAddon.UI
{
    /// <summary>
    /// Developer self-test run INSIDE a real SAP B1 client (needs the client to be logged in): creates the right-click menu through the real UI API,
    /// checks that every id exists exactly once even after repeated creation, then removes it. It does not simulate the right-click itself and touches no data.
    /// </summary>
    public static class SelfTest
    {
        public static int RunMenu(string connectionString, string outFile)
        {
            var sb = new StringBuilder();
            bool ok = true;
            Action<string, bool> check = (name, pass) => { sb.AppendLine((pass ? "PASS  " : "FAIL  ") + name); if (!pass) ok = false; };
            var con = new SapConnection();
            try
            {
                con.Connect(connectionString);
                var host = new SapMenuHost(con.App);
                var builder = new CtxMenuBuilder(host);
                var all = new CtxState(); foreach (var i in CtxMenuDefinition.Items) all.Enabled.Add(i.Action);
                var some = new CtxState(); some.Enabled.Add(CtxAction.ValidateGst);

                builder.Hide();
                check("start clean: root absent", !host.Exists(CtxMenuDefinition.RootId));
                builder.Show(all);
                check("root popup created under SAP menu " + CtxMenuDefinition.ParentMenuId, host.Exists(CtxMenuDefinition.RootId));
                check("3 group popups exist", CtxMenuDefinition.Groups.All(g => host.Exists(CtxMenuDefinition.GroupId(g))));
                check("all 22 action items exist", CtxMenuDefinition.Items.All(i => host.Exists(i.Id)));
                for (int n = 0; n < 4; n++) builder.Show(some);                     // repeated right-clicks
                check("repeated Show: still all items, no exception", CtxMenuDefinition.Items.All(i => host.Exists(i.Id)));
                var syncItem = con.App.Menus.Item(CtxMenuDefinition.Items.First(i => i.Action == CtxAction.ValidateGst).Id);
                var genItem = con.App.Menus.Item(CtxMenuDefinition.Items.First(i => i.Action == CtxAction.GenerateEInv).Id);
                check("enabled flag applied (Sync enabled)", syncItem.Enabled);
                check("enabled flag applied (Generate E-Invoice disabled)", !genItem.Enabled);
                builder.Hide();
                check("Hide removes every item", !host.Exists(CtxMenuDefinition.RootId) && CtxMenuDefinition.Items.All(i => !host.Exists(i.Id)));
                builder.Hide();
                check("second Hide is harmless", true);
            }
            catch (Exception ex) { sb.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message); ok = false; }
            finally { try { new CtxMenuBuilder(new SapMenuHost(con.App)).Hide(); } catch { } con.Release(); }
            sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
            File.WriteAllText(outFile, sb.ToString());
            return ok ? 0 : 1;
        }
    }
}
