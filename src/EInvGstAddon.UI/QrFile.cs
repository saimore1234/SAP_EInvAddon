using System;
using System.IO;
using System.Text.RegularExpressions;
using EInvGstAddon.Common;

namespace EInvGstAddon.UI
{
    /// <summary>Saves the signed e-Invoice QR of an invoice as a PNG so a Crystal report can load it (dynamic image location = U_QRPath on the invoice).</summary>
    public static class QrFile
    {
        public static string Folder(AppConfig cfg)
        {
            string f = cfg == null ? "" : (cfg.QrImageFolder ?? "").Trim();
            return f.Length > 0 ? f : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EInvGstAddon", "qr");
        }

        /// <summary>Returns the full path of the saved PNG, or "" when it could not be written (never throws; the IRN itself is already safe).</summary>
        public static string Write(AppConfig cfg, string database, int docEntry, string payload)
        {
            if (string.IsNullOrEmpty(payload)) return "";
            try
            {
                string dir = Folder(cfg);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "QR_" + Regex.Replace(database ?? "", @"[^A-Za-z0-9_-]", "_") + "_" + docEntry + ".png");
                string tmp = path + ".tmp";
                var data = new QRCoder.QRCodeGenerator().CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
                using (var bmp = new QRCoder.QRCode(data).GetGraphic(8))
                {
                    // Crystal sizes a dynamically loaded picture from its pixel size and resolution: state a 2.5 inch native size (all pixels kept).
                    bmp.SetResolution(bmp.Width / 2.5f, bmp.Height / 2.5f);
                    bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return path;
            }
            catch (Exception ex) { Logger.Warn("qr.file.failed", "docEntry", docEntry, "error", ex.Message); return ""; }
        }
    }
}
