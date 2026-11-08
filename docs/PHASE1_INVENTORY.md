# Phase 1 inventory (preserved, extended – not rewritten)

| Module | Where | Status in this release |
|---|---|---|
| ERPNext REST client (token auth, HTTPS only, error mapping, timeouts) | `EInvGstAddon.ERPNext/ErpClient.cs` | kept; company guard generalised (no names in code) |
| Custom-field setup on ERPNext | `ErpSchema.cs` | kept; + `custom_sap_config_id` |
| Customer / Address / Contact sync (CardCode mapping, never by name) | `Masters.cs` `CustomerSync` | kept; group/territory/currency discovered, not preset |
| Item sync (item_code = SAP ItemCode, HSN resolution without invention) | `Masters.cs` `ItemSync` | kept |
| Warehouse mapping with ownership validation | `Masters.cs` `WarehouseSync` | kept; root warehouse discovered |
| Draft Sales Invoice builder, exact SAP tax rows, precision adjustment | `InvoiceSync.cs` | kept; price-list currency/series/currency discovered |
| Idempotent engine, retry, lost-response reconciliation, queue | `SyncEngine.cs` | kept unchanged in behaviour |
| SAP read layer (read-only SELECTs through DI Recordset) | `Core/SapReader.cs` | kept |
| UDF/UDT installer, DI-API state store | `Database/` | kept; every row now scoped by `U_ConfigId`; + `@EINV_DOCS`, GST UDFs |
| Event wiring (`FormDataEvent` add of object 13), background queue, recovery timer | `UI/Host.cs`, `AddonServices.cs` | kept; configuration chosen by open SAP database |
| Screens: settings, sync lists, mappings, dashboard | `UI/` | settings rewritten for multiple configurations; dashboard extended |
| 21 Phase-1 offline tests + live tests | `Tests/` | all still pass |

Verified live in Phase 1 (VCPL draft invoices from the Vipra SAP database, incl. SAP DocEntry 2624 synchronised from the real client).
Migration: an existing single-company `config.json` is converted on first load to configuration id `default`, keeping its reference format
(`<db>-13-<DocEntry>`) so existing drafts are still found and no duplicate is created.
