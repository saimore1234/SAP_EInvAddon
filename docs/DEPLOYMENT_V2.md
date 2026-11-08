# Deployment and migration to 2.0.0

2.0 changes the architecture: **SAP → ERPNext GST API → GSP/IRP**. SAP invoices are no longer copied into ERPNext.

## 1. ERPNext (staging bench first)
1. `bench get-app <your repo>/frappe_app/sap_b1_direct_integration` (Frappe Cloud: add as private app) · `bench --site <site> install-app sap_b1_direct_integration` · `bench --site <site> migrate`.
2. Create the API user (integration role only) and a **SAP Integration Profile** per SAP company (see SANDBOX_TESTING.md step 4). Environment must match GST Settings sandbox mode.
3. Run the app tests on the staging site: `bench --site <test-site> run-tests --app sap_b1_direct_integration`.
4. Existing 1.x leftovers on the site (custom fields `custom_sap_*`, drafts created by 1.x, the `VCPL-SINV-…` naming series option) are not used and not modified by 2.0; remove them only after review.

## 2. SAP (each client)
1. Unzip `EInvGstAddon-2.0.0.zip`, run `Install.ps1` (elevated). Stop any running 1.x add-on first. The previous release package is kept untouched in `dist\`.
2. Start/registers `EInvGstAddon.exe`; open **ERPNext Connection Settings**. An existing 1.x configuration is migrated automatically (same SAP database, URL, key; Integration ID defaults to `SAPB1_<DATABASE>` – change it to the profile name you created).
3. **Check Integration Profile**, **Create SAP UDF/UDT** (adds `@EINV_TXN`, the GST status fields `U_GSTStatus/U_GSTSyncDate/U_GSTError/U_ERPRequestID`, keeps the 1.x GST fields `U_IRN`, `U_AckNo`, … ), save, log out/in.
5. **GST Configuration** → Validate → enable; **GST Tax Mapping** (SAP tax types, TCS, UQC units, freight SAC, document number pattern).
6. The old Sales-Invoice menu entries (Invoice Synchronization, Pending/Failed, mappings) disappear; their SAP tables remain but are unused. Previously synchronised SAP invoices simply start with status "Not generated" for GST.

## 3. Rollback
Install the previous package from `dist\` (1.2.0). ERPNext data written by 2.0 is limited to the two small DocTypes.

## 4. Operations
* Logs: `%LOCALAPPDATA%\EInvGstAddon\logs` (secrets masked). ERPNext: `logs/sap_b1_gst*.log` (ids and status only) and *SAP GST Transaction* list.
* Credentials: CREDENTIAL_ROTATION.md. Checklist: PRODUCTION_CHECKLIST.md. API: API.md. Versions: COMPATIBILITY.md, INDIA_COMPLIANCE_REUSE.md.
