# GST sandbox testing (do this before any production use)

Real IRNs / e-Way Bills must never be generated on a production site while testing. Nothing in this release has been run against a real IRP/GSP yet.

## What you need
1. A **separate ERPNext 15 site** (a Frappe Cloud test site is fine) with ERPNext, India Compliance 15.x and the `sap_b1_direct_integration` app installed on a **staging bench**.
2. **GST Settings** on that site: *Enable API*, *Enable e-Invoice*, *Enable e-Waybill*, **Sandbox Mode ON**, and a GST Credential row (service "e-Waybill / e-Invoice") for the seller GSTIN with the **sandbox** GSP username/password supplied by your GSP.
3. A dedicated API user (role with read/write on *SAP GST Transaction* and *SAP Integration Profile*, no accounting roles) with API key/secret.
4. A **SAP Integration Profile**: Integration ID (e.g. `SAPB1_TEST`), SAP database, ERPNext company (with the same GSTIN), seller GSTIN, **Environment = Sandbox**, the API user, permitted operations.
5. A SAP test company (not production books) with a few A/R invoices.

## Safety checks built in
* A Sandbox profile is refused when GST Settings are in production mode (and vice versa); the request's `environment` must equal the profile.
* The Sandbox GSP only accepts registered test identities, so the server replaces seller/buyer GSTIN/state/PIN with the sandbox identities **after** validating your real data and reports each replacement (`sandbox_substitutions`). The SAP confirmation dialog repeats this.

## Steps
1. In SAP: *ERPNext Connection Settings* → URL, API key/secret, Integration ID, Environment **Sandbox** → *Check Integration Profile* (must show profile found, API user matches, environment consistent) → *Create SAP UDF/UDT* → Save → log out/in.
2. *GST Configuration*: seller GSTIN, legal name, address, city, PIN, state → *Validate against ERPNext* (must say OK) → tick E-Invoice / E-Way Bill → Save.
3. *GST Tax Mapping*: confirm CGST/SGST/IGST types, map other charges (TCS), units (SAP unit → UQC), freight SAC, document number pattern (≤16 chars).
4. Right-click a test invoice → **Validate GST Invoice** → fix every listed finding → **Generate E-Invoice** → expect IRN, ack no/date; **View QR Code**, **Print**, **Download Signed Data**.
5. **Generate E-Way Bill** (transport dialog) → number + validity; **Update Vehicle**, **Update Transporter**; **Refresh**.
6. Cancellation within 24 h: **Cancel E-Way Bill**, **Cancel E-Invoice** (reported only when the IRP confirms).
7. Resilience: stop network briefly during a generate → status must become *Unknown*; then **Synchronize GST Data / Send Request** must reconcile without creating a second IRN.
8. Record results (screenshots of dialogs, IRN/EWB numbers, `SAP GST Transaction` rows) in the test report before requesting production go-live.

## Known to verify in the first sandbox run (not yet exercised)
Exact response keys of your GSP for `get_e_waybill` (`vehicleNo`, `status`), the lookup by date (`get_e_waybills_by_date`) used for e-Way Bill reconciliation, the e-Way Bill QR format printed on the SAP page, and sandbox acceptance of the generated payload (item totals, state codes).
