using System;
using System.Threading;
using System.Windows.Forms;
using EInvGstAddon.Common;
using EInvGstAddon.UI;

namespace EInvGstAddon
{
    internal static class Program
    {
        /// <summary>args[0] is the connection string passed by SAP Business One when it starts the add-on.</summary>
        [STAThread]
        private static int Main(string[] args)
        {
            Logger.Min = LogLevel.Info;
            Logger.Info("process.start", "x64", Environment.Is64BitProcess);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Logger.Error("unhandled.ui", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Logger.Error("unhandled.domain", e.ExceptionObject as Exception);

            bool created;
            using (new Mutex(true, "Local\\EInvGstAddon_Singleton", out created))
            {
                if (!created) { Logger.Warn("process.duplicate"); return 2; }
                if (args == null || args.Length == 0)
                {
                    MessageBox.Show("No SAP Business One connection string was supplied.\nThis add-on must be started by SAP Business One.", Constants.AddonName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                try { new AddonHost().Run(args[0]); }
                catch (Exception ex)
                {
                    Logger.Error("startup.failed", ex);
                    MessageBox.Show("The E-Invoice & ERPNext add-on could not start:\n" + ex.Message, Constants.AddonName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 3;
                }
            }
            return 0;
        }
    }
}
