using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using EInvGstAddon.Core;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.UI
{
    /// <summary>
    /// Printable e-Invoice / e-Way Bill pages. They contain only values returned by the IRP / e-Way Bill system (IRN, acknowledgement, signed QR,
    /// e-Way Bill number and validity) next to the SAP invoice data. The QR image is drawn from the signed QR payload the IRP returned.
    /// </summary>
    public static class PrintDocs
    {
        private static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }
        private static string N(JToken t) { decimal d; return t != null && decimal.TryParse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d.ToString("N2", CultureInfo.InvariantCulture) : ""; }

        private static string QrImg(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return "";
            var data = new QRCoder.QRCodeGenerator().CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
            using (var bmp = new QRCoder.QRCode(data).GetGraphic(6))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return "<img style='width:190px;height:190px' src='data:image/png;base64," + Convert.ToBase64String(ms.ToArray()) + "'/>";
            }
        }

        private const string Css = "<style>body{font-family:Segoe UI,Arial;font-size:12px;margin:24px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #999;padding:4px 6px;text-align:left}th{background:#eee}.r{text-align:right}h2{margin:0 0 6px 0}.box{border:1px solid #999;padding:8px;margin:8px 0}@media print{.noprint{display:none}}</style>";

        public static string EInvoice(JObject c, GstStatus s)
        {
            var sb = new StringBuilder("<html><head><meta charset='utf-8'><title>e-Invoice</title>" + Css + "</head><body>");
            sb.Append("<h2>e-Invoice</h2><div class='box'><table><tr><td><b>IRN</b></td><td>" + H(s.Irn) + "</td><td rowspan='3' style='width:200px;text-align:center'>" + QrImg(s.SignedQr) + "</td></tr>");
            sb.Append("<tr><td><b>Acknowledgement No / Date</b></td><td>" + H(s.AckNo) + " &nbsp; " + (s.AckDate.HasValue ? s.AckDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : "") + "</td></tr>");
            sb.Append("<tr><td><b>Document</b></td><td>" + H((string)c["document_type"]) + " " + H((string)c["document_number"]) + " dated " + H((string)c["document_date"]) + "</td></tr></table></div>");
            sb.Append("<table><tr><th>Seller</th><th>Buyer</th></tr><tr><td>" + Party(c["seller"]) + "</td><td>" + Party(c["buyer"]) + "</td></tr></table><br/>");
            sb.Append("<table><tr><th>#</th><th>Description</th><th>HSN</th><th class='r'>Qty</th><th>Unit</th><th class='r'>Taxable</th><th class='r'>GST %</th><th class='r'>CGST</th><th class='r'>SGST</th><th class='r'>IGST</th></tr>");
            foreach (var it in (JArray)c["items"])
            {
                decimal rate; decimal.TryParse(((it["igst_rate"] ?? 0).ToString() != "0" ? it["igst_rate"] : (it["cgst_rate"]?.ToObject<decimal>() * 2).ToString()).ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out rate);
                sb.Append("<tr><td>" + it["line"] + "</td><td>" + H((string)it["description"]) + "</td><td>" + H((string)it["hsn_code"]) + "</td><td class='r'>" + it["quantity"] + "</td><td>" + H((string)it["unit"]) + "</td><td class='r'>" + N(it["taxable_value"]) + "</td><td class='r'>" + rate + "</td><td class='r'>" + N(it["cgst_amount"]) + "</td><td class='r'>" + N(it["sgst_amount"]) + "</td><td class='r'>" + N(it["igst_amount"]) + "</td></tr>");
            }
            var t = c["tax_totals"];
            sb.Append("</table><p class='r'>Taxable " + N(t["taxable_value"]) + " | CGST " + N(t["cgst"]) + " | SGST " + N(t["sgst"]) + " | IGST " + N(t["igst"]) + " | Cess " + N(t["cess"]) + " | Other " + N(t["other_charges"]) + " | Round-off " + N(t["round_off"]) + " | <b>Total " + N(t["invoice_total"]) + "</b></p>");
            sb.Append("<p class='noprint'>Print this page from the browser (Ctrl+P).</p></body></html>");
            return sb.ToString();
        }

        public static string EWayBill(JObject c, GstStatus s)
        {
            var sb = new StringBuilder("<html><head><meta charset='utf-8'><title>e-Way Bill</title>" + Css + "</head><body><h2>e-Way Bill</h2><div class='box'><table>");
            sb.Append("<tr><td><b>E-Way Bill No</b></td><td>" + H(s.EwbNo) + "</td><td rowspan='4' style='width:200px;text-align:center'>" + QrImg(s.EwbNo + "/" + ((string)c["seller"]["gstin"]) + "/" + (s.EwbDate.HasValue ? s.EwbDate.Value.ToString("dd/MM/yyyy") : "")) + "</td></tr>");
            sb.Append("<tr><td><b>Generated</b></td><td>" + (s.EwbDate.HasValue ? s.EwbDate.Value.ToString("yyyy-MM-dd HH:mm") : "") + "</td></tr>");
            sb.Append("<tr><td><b>Valid until</b></td><td>" + (s.EwbValidUpto.HasValue ? s.EwbValidUpto.Value.ToString("yyyy-MM-dd HH:mm") : "") + "</td></tr>");
            sb.Append("<tr><td><b>Document</b></td><td>" + H((string)c["document_number"]) + " dated " + H((string)c["document_date"]) + "</td></tr></table></div>");
            var tr = c["transport"];
            sb.Append("<table><tr><th>From</th><th>To</th></tr><tr><td>" + Party(c["seller"]) + "</td><td>" + Party(c["buyer"]) + "</td></tr></table><br/>");
            sb.Append("<table><tr><th>Mode</th><th>Vehicle</th><th>Transporter ID</th><th>Transporter</th><th>Distance</th></tr><tr><td>" + H((string)tr?["transport_mode"]) + "</td><td>" + H(string.IsNullOrEmpty(s.VehicleNo) ? (string)tr?["vehicle_number"] : s.VehicleNo) + "</td><td>" + H((string)tr?["transporter_id"]) + "</td><td>" + H((string)tr?["transporter_name"]) + "</td><td>" + tr?["distance"] + " km</td></tr></table>");
            sb.Append("<p>Invoice value " + N(c["tax_totals"]["invoice_total"]) + "</p><p class='noprint'>Print this page from the browser (Ctrl+P).</p></body></html>");
            return sb.ToString();
        }

        private static string Party(JToken p)
        {
            if (p == null) return "";
            return "<b>" + H((string)p["legal_name"]) + "</b><br/>" + H((string)p["address"]) + "<br/>" + H((string)p["city"]) + " " + H((string)p["pincode"]) + "<br/>GSTIN " + H((string)p["gstin"]) + " &nbsp; State " + H((string)p["state_code"]);
        }
    }
}
