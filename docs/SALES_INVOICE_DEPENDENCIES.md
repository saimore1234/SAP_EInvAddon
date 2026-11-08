# Sales Invoice dependencies – inventory and removal (2.0.0)

Requirement: **no ERPNext Sales Invoice (or Customer, Item, Warehouse, Payment/Stock/accounting entry) is created or read for a SAP invoice.** SAP stays the only accounting system; ERPNext is a GST processing backend.

## What existed in 1.x (Phase 1/2) and what happened to it

| 1.x dependency | Where | 2.0 status |
|---|---|---|
| POST/GET `/api/resource/Sales Invoice` (draft creation, duplicate lookup, status) | `EInvGstAddon.ERPNext/InvoiceSync.cs`, `SyncEngine.cs` | **Not used by the add-on.** The classes remain in the library only because their 1.x unit tests are kept; no screen, menu, event or queue references them (proved by `NoSalesInvoiceDependencyTests`) |
| ERPNext Customer / Address / Contact creation | `Masters.cs` `CustomerSync` | not used |
| ERPNext Item / UOM / Item Group creation | `Masters.cs` `ItemSync` | not used |
| ERPNext Warehouse mapping | `Masters.cs` `WarehouseSync` | not used |
| Custom fields `custom_sap_*` on Sales Invoice / Customer / Item | `ErpSchema.cs` | not used, never created by 2.0. Fields created earlier on the live site are left untouched (no data was ever removed) |
| Invoice submission (`docstatus = 1`) | `GstService.SubmitInvoice` | removed from the workflow (there is nothing to submit) |
| india_compliance `generate_e_invoice(docname)`, `cancel_e_invoice`, `generate_e_waybill(doctype, docname)` etc. (document based) | `GstService.cs`, Frappe `api.py` v1 | **replaced**: the Frappe app now builds the NIC payload itself and calls the document-free GSP client classes (see INDIA_COMPLIANCE_REUSE.md) |
| `frappe.get_doc("Sales Invoice", ...)`, `frappe.db` on accounting tables | Frappe app v1 `api.py`, `events.py`, `utils.py`, `install.py`, `patches/` | **deleted** from the app (history stays in git) |
| Sales Invoice naming series, sandbox/production check via the invoice | v1 | replaced by profile based checks |
| SAP-side: `FormDataEvent` auto-sync of every new A/R Invoice, queue, retry timer, sync/mapping screens, `@EINV_SYNC/@EINV_CUSTMAP/@EINV_ITEMMAP/@EINV_WHSMAP` | `UI/Host.cs`, `AddonServices.cs`, `Forms.cs` | **removed from the add-on** (existing SAP tables are left in place, unused) |

## What 2.0 touches in ERPNext (complete list)
* DocType **SAP Integration Profile** (a few configuration rows) and **SAP GST Transaction** (one metadata row per GST operation, see API.md) – the only DocTypes the app writes.
* Reads: `Company` (GSTIN), `GST Settings` (sandbox flag, credential list), version info.
* `Integration Request` rows written by india_compliance itself for every GSP call (contains the request payload) – cleaned by a scheduled task after 30 days; see INDIA_COMPLIANCE_REUSE.md.

## How it is verified
* `NoSalesInvoiceDependencyTests` (C#): the UI project, `GstGateway`, `InvoiceJsonBuilder` and the menu model contain none of: Sales Invoice, `/api/resource`, SyncEngine, SyncQueue, InvoiceSync, CustomerSync, ItemSync, WarehouseSync, GstService, ErpSchema, `custom_sap_`; the Frappe app names none of the accounting / master DocTypes.
* `GstGatewayTests` (C#): every operation issues only `POST /api/method/sap_b1_direct_integration.api.gst.*`; the recorded paths never contain `/api/resource`.
* `NoAccountingDocumentsTests` (Python, AST based): the app's Frappe calls reference only its own two DocTypes plus Company / GST Settings / Integration Request.
* **Not yet possible here:** the live database assertion "number of Sales Invoice rows stays zero" needs a bench; `tests/test_bench_zero_sales_invoice.py` contains it for the first bench run.
