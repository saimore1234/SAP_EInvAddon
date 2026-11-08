using System;
using System.Collections.Generic;
using EInvGstAddon.Common;

namespace EInvGstAddon.UI
{
    /// <summary>Base class for the add-on screens (programmatic UI API forms).</summary>
    public abstract class FormBase
    {
        protected readonly AddonServices Svc;
        public SAPbouiCOM.Form Form { get; protected set; }
        public string Uid { get { return Form == null ? null : Form.UniqueID; } }
        private static int _seq;
        private readonly Dictionary<string, string> _ud = new Dictionary<string, string>();

        protected FormBase(AddonServices svc) { Svc = svc; }

        /// <summary>User-data-source IDs are limited to 9 characters: map each control id to a short one.</summary>
        private string Ud(string uid)
        {
            string n;
            if (!_ud.TryGetValue(uid, out n)) { n = "u" + (_ud.Count + 1); _ud[uid] = n; }
            return n;
        }

        protected SAPbouiCOM.Form Create(string formType, string title, int width, int height)
        {
            var cp = (SAPbouiCOM.FormCreationParams)Svc.App.CreateObject(SAPbouiCOM.BoCreatableObjectType.cot_FormCreationParams);
            cp.BorderStyle = SAPbouiCOM.BoFormBorderStyle.fbs_Sizable;
            cp.FormType = formType;
            cp.UniqueID = formType + "_" + (DateTime.Now.Ticks / 10000 % 10000000) + (++_seq);
            Form = Svc.App.Forms.AddEx(cp);
            Form.Title = title; Form.Width = width; Form.Height = height; Form.Left = 150; Form.Top = 60;
            return Form;
        }

        public void Show() { Form.Visible = true; Form.Select(); }

        protected SAPbouiCOM.Item Static(string uid, string caption, int l, int t, int w)
        {
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_STATIC);
            it.Left = l; it.Top = t; it.Width = w; it.Height = 15;
            ((SAPbouiCOM.StaticText)it.Specific).Caption = caption;
            return it;
        }

        protected SAPbouiCOM.Item Edit(string uid, int l, int t, int w, bool password = false, int len = 254)
        {
            Form.DataSources.UserDataSources.Add(Ud(uid), SAPbouiCOM.BoDataType.dt_SHORT_TEXT, len);
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_EDIT);
            it.Left = l; it.Top = t; it.Width = w; it.Height = 15;
            var e = (SAPbouiCOM.EditText)it.Specific;
            e.DataBind.SetBound(true, "", Ud(uid));
            if (password) e.IsPassword = true;
            return it;
        }

        protected SAPbouiCOM.Item DateEdit(string uid, int l, int t, int w)
        {
            Form.DataSources.UserDataSources.Add(Ud(uid), SAPbouiCOM.BoDataType.dt_DATE, 10);
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_EDIT);
            it.Left = l; it.Top = t; it.Width = w; it.Height = 15;
            ((SAPbouiCOM.EditText)it.Specific).DataBind.SetBound(true, "", Ud(uid));
            return it;
        }

        protected SAPbouiCOM.Item MultiLine(string uid, int l, int t, int w, int h)
        {
            Form.DataSources.UserDataSources.Add(Ud(uid), SAPbouiCOM.BoDataType.dt_LONG_TEXT, 0);
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_EXTEDIT);
            it.Left = l; it.Top = t; it.Width = w; it.Height = h;
            ((SAPbouiCOM.EditText)it.Specific).DataBind.SetBound(true, "", Ud(uid));
            return it;
        }

        protected SAPbouiCOM.Item Button(string uid, string caption, int l, int t, int w)
        {
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_BUTTON);
            it.Left = l; it.Top = t; it.Width = w; it.Height = 19;
            ((SAPbouiCOM.Button)it.Specific).Caption = caption;
            return it;
        }

        protected SAPbouiCOM.Item Check(string uid, string caption, int l, int t, int w)
        {
            Form.DataSources.UserDataSources.Add(Ud(uid), SAPbouiCOM.BoDataType.dt_SHORT_TEXT, 1);
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_CHECK_BOX);
            it.Left = l; it.Top = t; it.Width = w; it.Height = 15;
            var c = (SAPbouiCOM.CheckBox)it.Specific;
            c.Caption = caption; c.ValOn = "Y"; c.ValOff = "N";
            c.DataBind.SetBound(true, "", Ud(uid));
            return it;
        }

        /// <summary>Grid backed by a DataTable that is filled with a SQL query (executed by the UI API, read-only).</summary>
        protected SAPbouiCOM.Grid QueryGrid(string uid, string dtUid, int l, int t, int w, int h)
        {
            Form.DataSources.DataTables.Add(dtUid);
            var it = Form.Items.Add(uid, SAPbouiCOM.BoFormItemTypes.it_GRID);
            it.Left = l; it.Top = t; it.Width = w; it.Height = h;
            var g = (SAPbouiCOM.Grid)it.Specific;
            g.SelectionMode = SAPbouiCOM.BoMatrixSelect.ms_Single;
            return g;
        }

        protected void LoadGrid(string uid, string dtUid, string sql)
        {
            var dt = Form.DataSources.DataTables.Item(dtUid);
            dt.ExecuteQuery(sql);
            var g = (SAPbouiCOM.Grid)Form.Items.Item(uid).Specific;
            g.DataTable = dt;
            for (int i = 0; i < g.Columns.Count; i++) g.Columns.Item(i).Editable = false;
            g.AutoResizeColumns();
        }

        /// <summary>Value of a column in the selected grid row, or null.</summary>
        protected object SelectedValue(string gridUid, string dtUid, string column)
        {
            var g = (SAPbouiCOM.Grid)Form.Items.Item(gridUid).Specific;
            if (g.Rows.SelectedRows.Count == 0) return null;
            int row = g.GetDataTableRowIndex(g.Rows.SelectedRows.Item(0, SAPbouiCOM.BoOrderType.ot_RowOrder));
            return Form.DataSources.DataTables.Item(dtUid).GetValue(column, row);
        }

        protected void Set(string uid, string value) { Form.DataSources.UserDataSources.Item(Ud(uid)).Value = value ?? ""; }
        protected string Get(string uid) { return (Form.DataSources.UserDataSources.Item(Ud(uid)).Value ?? "").Trim(); }
        protected void Caption(string uid, string text) { ((SAPbouiCOM.StaticText)Form.Items.Item(uid).Specific).Caption = text; }

        protected void Log(string boxUid, string line)
        {
            string cur = Get(boxUid);
            Set(boxUid, (cur.Length == 0 ? "" : cur + "\r") + DateTime.Now.ToString("HH:mm:ss") + "  " + line);
        }

        public abstract void OnItemEvent(SAPbouiCOM.ItemEvent e, out bool bubble);
        public virtual void OnClosed() { }
    }
}
