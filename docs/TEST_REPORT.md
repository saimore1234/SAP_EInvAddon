# Test report – generic release + right-click menu (72 automated tests, all passing)

Run: `dotnet test src/EInvGstAddon.Tests` (offline; mock ERPNext/IRP; no real GST call, no customer credential).

| Area | Tests | Result |
|---|---|---|
| A. Configuration | legacy file migration & reference format; per-profile id in references; one active profile per SAP DB and lookup by open DB; secrets never in clear text; protected company; GST automation gated by validation | pass |
| B. Invoice sync | new/existing customer & item, warehouse ownership, tax mapping & missing tax, duplicate prevention, ERPNext down → Pending → recovery, lost response after create, HSN resolution (parent/heading codes, repair of own item), precision adjustment, cancelled/service invoices | pass |
| Isolation | second fictional customer (other DB/company/abbr): own company, reference, warehouse root; another company's account/warehouse refused (auto-detected from the site's company list); two configurations on one site do not collide | pass |
| C. E-Invoice (mock) | draft blocked; disabled in configuration blocked; generation only via `generate_e_invoice`, values read back; existing IRN never regenerated; IRP rejection reported with no IRN invented; lost response → one call, status re-read; sandbox/production mismatch blocked; missing GSP credentials/GSTIN; B2C; cancellation (reason, 24 h, confirmed by ERPNext); invalid app version; other company's invoice refused | pass |
| D. E-Way Bill (mock) | validation (vehicle, mode, distance, transporter) before any call; generate; update vehicle; update transporter; cancel | pass |
| Live (earlier, Phase 1) | connection, customer/item/warehouse/tax/draft creation, totals, duplicate/retry against the real site and SAP DocEntry 2624 | pass |

## NOT verified (be explicit)
* **No sandbox GST run**: no sandbox GSP credentials were available, and the only ERPNext site is production-configured. E-Invoice / E-Way Bill are tested against a mock of the *installed method signatures* only. First real run must be on a sandbox site.
* **SAP screens** (settings with profiles, E-Invoice, E-Way Bill, registers, GST configuration, tax mapping, dashboard) and the new DI-API persistence (`@EINV_DOCS`, GST UDFs, `U_ConfigId`) **compile but have not been exercised in a SAP client**; the UDT/UDF creation for the new objects has not been run (re-run *Create SAP UDF/UDT* in Vipra adds them – it changes that database).
* **Frappe app** `sap_b1_direct_integration` is written from the source of Frappe 15 / India Compliance 15.29.0 but **has not been executed on a bench** (none available; no Python on the build machine). Its tests are placeholders.
* **Automatic E-Invoice / E-Way Bill generation after sync** is not wired (switches exist and are gated).
* Charts: the dashboard shows KPI cards and summary tables, not graphical charts (SAP UI API has no chart control).
* Tax *templates* are not mapped (exact SAP amounts are posted as tax rows); per-line CGST/SGST splits are not reproduced.
* Rate-precision difference (shared ERPNext setting) still carried on a round-off account.

## Right-click menu (this change)
| Area | Result |
|---|---|
| Menu model: 21 actions, 3 groups, unique ids; creation under SAP menu 1280; removal; **no duplicates after repeated right-clicks** (fake menu host) | pass (automated) |
| Same menu creation/duplicate/removal executed through the **real SAP UI API inside the running SAP B1 client** (`EInvGstAddon.exe <conn> --selftest-menu`, 9 checks incl. enabled/disabled flags) | **PASS** |
| Invoice identification by DocEntry (DocNum alone rejected), only saved A/R invoices in view mode of the configured company, other document in front refused, one action per invoice at a time | pass (automated) |
| Context-aware enabling: not synced / Draft / submitted / IRN present (24 h) / E-Way Bill present (validity, 24 h) / GST disabled / cancelled | pass (automated) |
| Detailed E-Invoice validation (17 checks), E-Way Bill validation, duplicate prevention, submit-twice prevention, refresh never generates, multi-company refusal, connection failure | pass (automated, mock ERPNext) |
| **NOT verified**: the actual right-click event, clicking the items, the dialogs and actions end-to-end in SAP, form refresh after actions, status-cache behaviour with a live ERPNext. Real IRN / E-Way Bill generation (needs a sandbox site). |  |
