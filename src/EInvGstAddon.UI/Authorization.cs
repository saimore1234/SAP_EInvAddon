using System;
using System.Linq;
using EInvGstAddon.Common;
using EInvGstAddon.Core;

namespace EInvGstAddon.UI
{
    /// <summary>Configuration-level authorization (by SAP user): administrators change configuration; GST users run GST actions.</summary>
    public static class Authorization
    {
        public static bool IsSuperUser(ISapSource sap, string user)
        {
            try
            {
                var r = ((SapReader)sap).Query1("SELECT SUPERUSER FROM OUSR WHERE USER_CODE=" + SapReader.Lit(user));
                return r != null && Convert.ToString(r["SUPERUSER"]).Trim() == "Y";
            }
            catch (Exception ex) { Logger.Warn("auth.superuser.failed", "error", ex.Message); return false; }
        }

        public static bool IsAdmin(AddonServices s)
        {
            string user = s.Sap.UserName;
            return IsSuperUser(s.Source, user) || s.Cfg.AdminUsers.Any(u => string.Equals(u, user, StringComparison.OrdinalIgnoreCase));
        }

        public static bool CanRunGst(AddonServices s)
        {
            if (s.Cfg.GstUsers.Count == 0) return true;
            string user = s.Sap.UserName;
            return IsAdmin(s) || s.Cfg.GstUsers.Any(u => string.Equals(u, user, StringComparison.OrdinalIgnoreCase));
        }
    }
}
