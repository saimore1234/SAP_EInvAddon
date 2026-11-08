# User manual

Menu: **Modules → E-Invoice & ERPNext Integration**

| Screen | Use |
|---|---|
| ERPNext Connection Settings | Create / edit / activate / delete configurations (SAP company database + ERPNext URL + credentials + company). Test, fetch lists, create SAP and ERPNext objects. Only a SAP superuser or a listed administrator can change it |
| Tax & Account Mapping | SAP tax type → ERPNext account (CGST −100, SGST −110, IGST −120, cess/other), freight, round-off, income account, cost centre. "Fetch from ERPNext GST Settings" pre-fills the GST accounts |
| GST Configuration | Supplier GSTIN, names, state, PIN, environment, e-Invoice / e-Way Bill switches. **Validate against ERPNext** must succeed before GST features or GST automation can be enabled |
| Dashboard | KPIs, daily/monthly tables, GST tax summary, filters (date, customer); always for the active configuration |
| Invoice Synchronization / Pending / Failed / Sync Logs / Integration History | Queue and results; Retry; synchronise an older invoice by DocEntry |
| Customer / Item / Warehouse Mapping | The permanent SAP ↔ ERPNext mapping of the active configuration |
| Generate E-Invoice | Load a synchronised invoice by DocNum → Validate → (Submit in ERPNext) → Generate → IRN, Ack, QR, signed invoice, print, cancel |
| Generate E-Way Bill | Transport data → Validate → Generate → number, date, validity; update vehicle / transporter; PDF; cancel |
| E-Invoice Register / E-Way Bill Register | Search, filter, refresh status, open SAP document, export to Excel (CSV) |

## Daily flow
1. Add an A/R Invoice in SAP → it is queued and appears in ERPNext as a **Draft** (status bar + invoice fields *ERPNext Invoice*, *Sync Status*).
2. Review the Draft in ERPNext. In **Generate E-Invoice** click **Submit in ERPNext** (explicit, confirmed – the add-on never submits by itself).
3. **Validate**, then **Generate E-Invoice**. The IRN / acknowledgement / signed QR are read back from ERPNext (they come from the IRP through the GST app). View QR shows the signed QR returned by the IRP.
4. **Generate E-Way Bill** screen: enter transporter / vehicle / distance, **Validate**, **Generate**.
5. Refresh, download, print or cancel (reason required; 24-hour rule enforced; cancelling an IRN also cancels the ERPNext invoice – the SAP invoice is never changed).

## Messages you will see
* *Draft … Submit it in ERPNext first* – GST needs a submitted invoice.
* *Tax configuration missing* – map the SAP tax type (e.g. TCS) in Tax & Account Mapping.
* *No usable HSN/SAC* – set HSN on the SAP item, or add the code in ERPNext; the add-on never invents one.
* *UNCERTAIN* – the portal answer was lost; nothing was retried. Click **Refresh Status**.
* *PRODUCTION … Blocked* – configuration says Sandbox but ERPNext GST Settings is live.

## GST automation
Switches *Automatic E-Invoice / E-Way Bill* are off by default and can be enabled only after GST validation succeeded.
(In this release the switches are stored and gated; the automatic trigger itself is not wired – see TEST_REPORT.)

## Right-click menu on the A/R Invoice (individual invoice operations)
Open a **saved** A/R Invoice (view mode), right-click → **E-Invoice & E-Way Bill**:

* **ERPNext** – Synchronize with ERPNext · Submit Invoice in ERPNext · View ERPNext Invoice (opens it in the browser) · View Integration History
* **E-Invoice** – Validate · Generate · View IRN Details · View Signed QR Code · Download Signed Invoice · Print E-Invoice · Refresh IRN Status · Cancel E-Invoice
* **E-Way Bill** – Validate · Generate · View Details · Update Vehicle Number · Update Transporter · Download · Print · Refresh Status · Cancel

Items that do not apply are greyed out (not synchronized → only Synchronize; Draft → Submit; submitted without IRN → Validate/Generate; IRN present → IRN views, print, refresh, cancel within 24 h; E-Way Bill present → details, vehicle/transporter update, cancel within 24 h; GST not enabled → GST items off).
The menu is drawn from the last ERPNext status read in the background (refreshed when the invoice is opened or right-clicked, at most every 30 s); **every action re-reads ERPNext live and re-checks before doing anything**.
The invoice is identified by **DocEntry** and the add-on refuses to act if a different document is in front when you choose an item.
Network calls never block SAP; results appear in dialogs, the status bar and the invoice's user-defined fields (the screen is refreshed after changes).
An **uncertain** generation (answer lost) blocks further generation until *Refresh IRN Status* has read the status successfully.
The separate screens, registers and dashboard remain available for monitoring and bulk work.
