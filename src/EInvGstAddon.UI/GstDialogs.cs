using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using EInvGstAddon.Core;
using EInvGstAddon.ERPNext;

namespace EInvGstAddon.UI
{
    /// <summary>Small modal dialogs used by the right-click actions. Always created on the SAP UI thread (callers post to it).</summary>
    public static class Dialogs
    {
        private static Form NewForm(string title, int w, int h)
        {
            return new Form { Text = title, Width = w, Height = h, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = new Font("Segoe UI", 9f) };
        }

        public sealed class Btn { public string Caption; public Action Click; public bool Close; }

        /// <summary>Read-only text with optional action buttons (Copy IRN, Refresh, ...).</summary>
        public static void Info(string title, string text, params Btn[] buttons)
        {
            using (var f = NewForm(title, 640, 440))
            {
                var tb = new TextBox { Left = 10, Top = 10, Width = 604, Height = 330, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text.Replace("\n", "\r\n"), BackColor = Color.White };
                f.Controls.Add(tb);
                int x = 10;
                foreach (var b in buttons ?? new Btn[0])
                {
                    var bb = b;
                    var btn = new Button { Text = b.Caption, Left = x, Top = 352, Width = Math.Max(90, b.Caption.Length * 8 + 16), Height = 28 };
                    btn.Click += (s, e) => { try { if (bb.Click != null) bb.Click(); } catch (Exception ex) { MessageBox.Show(ex.Message, title); } if (bb.Close) f.Close(); };
                    f.Controls.Add(btn); x += btn.Width + 6;
                }
                var close = new Button { Text = "Close", Left = 524, Top = 352, Width = 90, Height = 28, DialogResult = DialogResult.OK };
                f.Controls.Add(close); f.AcceptButton = close;
                f.ShowDialog();
            }
        }

        /// <summary>Validation / check results: one line per check, errors individually.</summary>
        public static void Checks(string title, IList<GstService.CheckItem> items, string footer)
        {
            using (var f = NewForm(title, 760, 520))
            {
                var lv = new ListView { Left = 10, Top = 10, Width = 724, Height = 400, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
                lv.Columns.Add("", 28); lv.Columns.Add("Check", 220); lv.Columns.Add("Result", 460);
                foreach (var c in items)
                {
                    var li = new ListViewItem(c.Ok ? (c.Warning ? "!" : "OK") : "X");
                    li.SubItems.Add(c.Name); li.SubItems.Add(c.Message ?? "");
                    li.ForeColor = c.Ok ? (c.Warning ? Color.DarkOrange : Color.DarkGreen) : Color.Firebrick;
                    lv.Items.Add(li);
                }
                int bad = items.Count(i => !i.Ok);
                var lbl = new Label { Left = 10, Top = 418, Width = 724, Height = 40, Text = (bad == 0 ? "All checks passed. " : bad + " check(s) failed - fix them before generating. ") + footer };
                var close = new Button { Text = "Close", Left = 644, Top = 456, Width = 90, DialogResult = DialogResult.OK };
                f.Controls.AddRange(new Control[] { lv, lbl, close }); f.AcceptButton = close;
                f.ShowDialog();
            }
        }

        public sealed class Field
        {
            public string Key, Label, Default; public string[] Options; public bool ReadOnly;
        }

        /// <summary>Input dialog. Returns null if cancelled. Extra buttons (e.g. Validate) run without closing it.</summary>
        public static Dictionary<string, string> Input(string title, string intro, IList<Field> fields, string okCaption, Func<Dictionary<string, string>, bool> validateOnOk, params Btn[] extra)
        {
            Dictionary<string, string> result = null;
            using (var f = NewForm(title, 560, 190 + fields.Count * 30 + (string.IsNullOrEmpty(intro) ? 0 : 50)))
            {
                int y = 10;
                if (!string.IsNullOrEmpty(intro)) { f.Controls.Add(new Label { Left = 10, Top = y, Width = 524, Height = 44, Text = intro }); y += 50; }
                var ctl = new Dictionary<string, Control>();
                foreach (var fl in fields)
                {
                    f.Controls.Add(new Label { Left = 10, Top = y + 3, Width = 170, Text = fl.Label });
                    Control c;
                    if (fl.Options != null && !fl.ReadOnly) { var cb = new ComboBox { Left = 190, Top = y, Width = 340, DropDownStyle = ComboBoxStyle.DropDownList }; cb.Items.AddRange(fl.Options); cb.Text = fl.Default ?? ""; if (cb.SelectedIndex < 0 && cb.Items.Count > 0 && string.IsNullOrEmpty(fl.Default)) cb.SelectedIndex = 0; c = cb; }
                    else c = new TextBox { Left = 190, Top = y, Width = 340, Text = fl.Default ?? "", ReadOnly = fl.ReadOnly, BackColor = fl.ReadOnly ? SystemColors.Control : Color.White };
                    ctl[fl.Key] = c; f.Controls.Add(c); y += 30;
                }
                Func<Dictionary<string, string>> collect = () => ctl.ToDictionary(k => k.Key, k => (k.Value.Text ?? "").Trim());
                int x = 10;
                foreach (var b in extra ?? new Btn[0])
                {
                    var bb = b;
                    var btn = new Button { Text = b.Caption, Left = x, Top = y + 10, Width = 110, Height = 28 };
                    btn.Click += (s, e) => { try { if (bb.Click != null) bb.Click(); } catch (Exception ex) { MessageBox.Show(ex.Message, title); } };
                    f.Controls.Add(btn); x += 116;
                }
                var ok = new Button { Text = okCaption, Left = 330, Top = y + 10, Width = 110, Height = 28 };
                var cancel = new Button { Text = "Cancel", Left = 446, Top = y + 10, Width = 84, Height = 28, DialogResult = DialogResult.Cancel };
                ok.Click += (s, e) => { var v = collect(); if (validateOnOk != null && !validateOnOk(v)) return; result = v; f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.AddRange(new Control[] { ok, cancel }); f.CancelButton = cancel;
                f.Tag = collect;
                CurrentCollector = collect;
                f.ShowDialog();
                CurrentCollector = null;
            }
            return result;
        }

        /// <summary>Lets an extra button (e.g. Validate) read the values typed so far.</summary>
        public static Func<Dictionary<string, string>> CurrentCollector;

        public static bool Confirm(string title, string text)
        {
            return MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        public static void Message(string title, string text, bool error = false)
        {
            MessageBox.Show(text, title, MessageBoxButtons.OK, error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------------- QR (the real signed payload) with print
        public static void Qr(GstStatus s)
        {
            if (string.IsNullOrEmpty(s.SignedQr)) { Message("Signed QR Code", "ERPNext holds no signed QR code for this invoice."); return; }
            using (var f = NewForm("Signed QR Code - " + s.SalesInvoice, 470, 650))
            {
                var data = new QRCoder.QRCodeGenerator().CreateQrCode(s.SignedQr, QRCoder.QRCodeGenerator.ECCLevel.M);
                var bmp = new QRCoder.QRCode(data).GetGraphic(6);
                var pic = new PictureBox { Image = bmp, SizeMode = PictureBoxSizeMode.Zoom, Left = 10, Top = 10, Width = 440, Height = 440 };
                var txt = new TextBox { Left = 10, Top = 458, Width = 440, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = "IRN: " + s.Irn + "\r\nAck No: " + s.AckNo + "\r\nAck Date: " + (s.AckDate.HasValue ? s.AckDate.Value.ToString("yyyy-MM-dd HH:mm") : "") + "\r\n\r\n" + s.SignedQr };
                var save = new Button { Text = "Save PNG", Left = 10, Top = 570, Width = 100, Height = 28 };
                var print = new Button { Text = "Print", Left = 116, Top = 570, Width = 100, Height = 28 };
                var close = new Button { Text = "Close", Left = 360, Top = 570, Width = 90, Height = 28, DialogResult = DialogResult.OK };
                save.Click += (a, b) => { using (var d = new SaveFileDialog { FileName = "qr-" + s.SalesInvoice.Replace('/', '-') + ".png", Filter = "PNG|*.png" }) if (d.ShowDialog() == DialogResult.OK) bmp.Save(d.FileName, System.Drawing.Imaging.ImageFormat.Png); };
                print.Click += (a, b) =>
                {
                    var pd = new PrintDocument();
                    pd.PrintPage += (o, e) =>
                    {
                        e.Graphics.DrawString("e-Invoice QR - " + s.SalesInvoice + "   IRN " + s.Irn, new Font("Segoe UI", 10f), Brushes.Black, 40, 40);
                        e.Graphics.DrawImage(bmp, 40, 80, 400, 400);       // the real signed QR, enlarged for scanning
                    };
                    using (var dlg = new PrintDialog { Document = pd }) if (dlg.ShowDialog() == DialogResult.OK) pd.Print();
                };
                f.Controls.AddRange(new Control[] { pic, txt, save, print, close }); f.AcceptButton = close;
                f.ShowDialog();
            }
        }

        // ---------------------------------------------------------------- files
        public static void SaveText(string suggestedName, string text)
        {
            if (string.IsNullOrEmpty(text)) { Message("Download", "ERPNext holds no such document for this invoice."); return; }
            using (var dlg = new SaveFileDialog { FileName = suggestedName, Filter = "Text/JSON|*.txt;*.json|All files|*.*" })
            { if (dlg.ShowDialog() != DialogResult.OK) return; File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false)); Message("Download", "Saved " + dlg.FileName); }
        }

        public static void SaveOrOpenBytes(string suggestedName, byte[] bytes, bool open)
        {
            string path;
            if (open) path = Path.Combine(Path.GetTempPath(), suggestedName);
            else
            {
                using (var dlg = new SaveFileDialog { FileName = suggestedName, Filter = "All files|*.*" })
                { if (dlg.ShowDialog() != DialogResult.OK) return; path = dlg.FileName; }
            }
            File.WriteAllBytes(path, bytes);
            if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Message("Download", "Saved " + path);
        }
    }
}
