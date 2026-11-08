using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EInvGstAddon.Common;
using EInvGstAddon.Core;
using SAPbobsCOM;

namespace EInvGstAddon.Database
{
    /// <summary>
    /// DI API access that shares the logged-in SAP B1 session (no extra password): the Company object comes from the UI API.
    /// Every call must run on the add-on's main STA thread; <see cref="IUiInvoker"/> marshals worker-thread requests there.
    /// </summary>
    public sealed class DiSession
    {
        public Company Company { get; private set; }
        public IUiInvoker Ui { get; private set; }

        public DiSession(object diCompanyFromUiApi, IUiInvoker ui)
        {
            Company = (Company)diCompanyFromUiApi;
            Ui = ui;
            if (!Company.Connected) throw new InvalidOperationException("The SAP DI API company object is not connected.");
        }

        public string DatabaseName { get { return Company.CompanyDB; } }

        public string LastError()
        {
            int code; string msg;
            Company.GetLastError(out code, out msg);
            return code + ": " + msg;
        }

        public List<Dictionary<string, object>> Query(string sql)
        {
            Recordset rs = (Recordset)Company.GetBusinessObject(BoObjectTypes.BoRecordset);
            try
            {
                rs.DoQuery(sql);
                var rows = new List<Dictionary<string, object>>();
                int n = rs.Fields.Count;
                while (!rs.EoF)
                {
                    var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < n; i++)
                    {
                        var f = rs.Fields.Item(i);
                        object v = f.Value;
                        if (v is DateTime && ((DateTime)v).Year < 1901) v = null;     // SAP empty date
                        d[f.Name] = v is string && ((string)v).Length == 0 ? null : v;
                    }
                    rows.Add(d);
                    rs.MoveNext();
                }
                return rows;
            }
            finally { Marshal.ReleaseComObject(rs); }
        }

        public object Scalar(string sql)
        {
            var rows = Query(sql);
            if (rows.Count == 0) return null;
            foreach (var v in rows[0].Values) return v;
            return null;
        }
    }

    /// <summary>ISqlRunner over the DI API Recordset, executed on the UI thread.</summary>
    public sealed class DiRunner : ISqlRunner
    {
        private readonly DiSession _di;
        public DiRunner(DiSession di) { _di = di; }
        public List<Dictionary<string, object>> Query(string sql) { return _di.Ui.Invoke(() => _di.Query(sql)); }
    }
}
