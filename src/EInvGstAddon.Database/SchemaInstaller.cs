using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using SAPbobsCOM;

namespace EInvGstAddon.Database
{
    /// <summary>Creates the add-on's UDFs and UDTs through the DI API. Idempotent: existing objects are left alone.</summary>
    public sealed class SchemaInstaller
    {
        private readonly DiSession _di;
        public SchemaInstaller(DiSession di) { _di = di; }

        private struct Fld
        {
            public string Name, Desc; public BoFieldTypes Type; public int Size; public BoFldSubTypes Sub;
            public Fld(string n, string d, BoFieldTypes t, int s = 0, BoFldSubTypes sub = BoFldSubTypes.st_None) { Name = n; Desc = d; Type = t; Size = s; Sub = sub; }
        }

        private static Fld A(string n, string d, int size) { return new Fld(n, d, BoFieldTypes.db_Alpha, size); }

        public List<string> Install()
        {
            var log = new List<string>();
            // --- UDTs ---
            EnsureTable(Constants.UdtSync, "ERPNext invoice sync", log);
            EnsureTable(Constants.UdtCustMap, "ERPNext customer map", log);
            EnsureTable(Constants.UdtItemMap, "ERPNext item map", log);
            EnsureTable(Constants.UdtWhsMap, "ERPNext warehouse map", log);

            EnsureFields("@" + Constants.UdtSync, new[]
            {
                new Fld("DocEntry", "SAP DocEntry", BoFieldTypes.db_Numeric, 11),
                new Fld("DocNum", "SAP DocNum", BoFieldTypes.db_Numeric, 11),
                A("ConfigId", "Configuration Id", 20), A("Database", "SAP Database", 30), A("ErpInv", "ERPNext Invoice ID", 60), A("Customer", "Customer Mapping", 140),
                A("Status", "Sync Status", 20), A("SyncTs", "Sync Date/Time", 20), A("Error", "Error Message", 254),
                new Fld("Retry", "Retry Count", BoFieldTypes.db_Numeric, 5)
            }, log);
            var mapFields = new[] { A("ConfigId", "Configuration Id", 20), A("SapKey", "SAP Key", 100), A("ErpName", "ERPNext Name", 140), A("Created", "Created By Add-on", 1), A("Note", "Note", 254) };
            EnsureFields("@" + Constants.UdtCustMap, mapFields, log);
            EnsureFields("@" + Constants.UdtItemMap, mapFields, log);
            EnsureFields("@" + Constants.UdtWhsMap, mapFields, log);

            // --- UDFs on A/R Invoice (OINV) ---
            EnsureFields("OINV", new[]
            {
                A(Constants.UdfErpDoc, "ERPNext Invoice", 60), A(Constants.UdfErpStatus, "ERPNext Sync Status", 20),
                A(Constants.UdfLastSync, "Last Sync", 20), A(Constants.UdfSyncError, "Sync Error", 254)
            }, log);
            return log;
        }

        private void EnsureTable(string name, string desc, List<string> log)
        {
            object exists = _di.Scalar("SELECT COUNT(*) FROM OUTB WHERE TableName='" + name + "'");
            if (Convert.ToInt32(exists) > 0) return;
            var ut = (UserTablesMD)_di.Company.GetBusinessObject(BoObjectTypes.oUserTables);
            try
            {
                ut.TableName = name; ut.TableDescription = desc; ut.TableType = BoUTBTableType.bott_NoObject;
                int rc = ut.Add();
                if (rc != 0) throw new InvalidOperationException("Creating UDT @" + name + " failed: " + _di.LastError());
                log.Add("UDT @" + name + " created");
            }
            finally { Marshal.ReleaseComObject(ut); }
        }

        private void EnsureFields(string table, IEnumerable<Fld> fields, List<string> log)
        {
            foreach (var f in fields)
            {
                object exists = _di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='" + table + "' AND AliasID='" + f.Name + "'");
                if (Convert.ToInt32(exists) > 0) continue;
                var uf = (UserFieldsMD)_di.Company.GetBusinessObject(BoObjectTypes.oUserFields);
                try
                {
                    uf.TableName = table; uf.Name = f.Name; uf.Description = f.Desc; uf.Type = f.Type; uf.SubType = f.Sub;
                    if (f.Size > 0) uf.EditSize = f.Size;
                    int rc = uf.Add();
                    if (rc != 0) throw new InvalidOperationException("Creating field U_" + f.Name + " on " + table + " failed: " + _di.LastError());
                    log.Add("UDF U_" + f.Name + " on " + table + " created");
                }
                finally { Marshal.ReleaseComObject(uf); }
            }
        }

        /// <summary>True when every table and field exists.</summary>
        public bool IsInstalled()
        {
            foreach (var t in new[] { Constants.UdtSync, Constants.UdtCustMap, Constants.UdtItemMap, Constants.UdtWhsMap })
                if (Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM OUTB WHERE TableName='" + t + "'")) == 0) return false;
            return Convert.ToInt32(_di.Scalar("SELECT COUNT(*) FROM CUFD WHERE TableID='OINV' AND AliasID IN ('" + Constants.UdfErpDoc + "','" + Constants.UdfErpStatus + "','" + Constants.UdfLastSync + "','" + Constants.UdfSyncError + "')")) == 4;
        }
    }
}
