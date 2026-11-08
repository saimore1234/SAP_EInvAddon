# Deployment guide (same package for every customer)

## Prerequisites
* SAP Business One 10.0, 64-bit client; DI API 10.0 (64-bit); .NET Framework 4.8.
* An ERPNext 15 site with the India Compliance app (only needed for GST features), reachable over **HTTPS**.
* ERPNext API user with a key/secret: Sales Invoice, Customer, Item, Address, Contact, Warehouse, UOM, groups – create/read; Custom Field read/create (first setup); Company/Account/Price List read.
  Give the user **User Permissions → Company** for the intended company only.
* A SAP user able to create UDF/UDT (first setup only).

## Build and package (developer machine)
`powershell -File deploy\Build-Release.ps1 -Version 1.1.0` → builds with Visual Studio MSBuild, runs all tests, creates `dist\EInvGstAddon-1.1.0.zip`.

## Install (customer PC)
1. Unzip, run `Install.ps1` elevated (copies to `C:\Program Files\EInvGstAddon`).
2. Register `EInvGstAddon.exe` in SAP *Add-On Administration* (ARD file from SAP's registration tool).
3. Log in to the customer's SAP company; open **ERPNext Connection Settings**; enter URL, key, secret, ERPNext company (use *Fetch Companies*); *Create SAP UDF/UDT*; *Create ERPNext Fields*; *Test Connection*; *Test Authentication*; **Save Configuration** (validated against the site). Log out/in.
4. **Tax & Account Mapping** → *Fetch from ERPNext GST Settings*, add freight / round-off accounts, Save.
5. Add a test invoice. GST: complete **GST Configuration** later and *Validate*.

## Several customers / companies
One installation serves any number of SAP companies: every configuration holds its own SAP database, ERPNext URL, credentials and company; the one that matches the **open SAP company database** is active. Mappings, queue, records and references are scoped by the configuration id. Only one *active* configuration per SAP database.

## Frappe app (optional until Phase 2 hardening)
`frappe_app/sap_b1_direct_integration` – install on a **staging** bench first (see its README). It adds a unique index on the SAP reference and server-side company authorisation.

## Security checklist
HTTPS only · secret DPAPI-encrypted per Windows user · logs mask secrets · `config.json` and logs are outside the repo · dedicated restricted ERPNext user · rotate per `CREDENTIAL_ROTATION.md` · configuration screens limited to SAP superusers/admins.

## Troubleshooting
| Symptom | Check |
|---|---|
| Add-on does not start | log `%LOCALAPPDATA%\EInvGstAddon\logs`; an old process may hold the mutex (kill `EInvGstAddon.exe`) |
| "Internal Error … ODBC -2001" right after creating UDFs | log out of SAP and in again |
| Menu works but nothing syncs | no active configuration for the open company database; Settings → Save; restart |
| `Safety stop … belongs to company` | an account/warehouse of another company was mapped; fix the mapping |
