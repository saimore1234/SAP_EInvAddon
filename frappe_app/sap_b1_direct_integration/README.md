# sap_b1_direct_integration (generic Frappe app)

Server side of the EInvGstAddon SAP Business One add-on. Contains **no GST logic** (calls the installed `india_compliance`) and **no customer-specific value**.

* API: `sap_b1_direct_integration.api.*` (see docstrings). All methods verify company access from ERPNext **User Permissions (Company)** of the API user - a user without Company permissions gets nothing.
* Duplicate protection: DB unique index on `Sales Invoice.custom_sap_sync_reference` (patch), a `validate` hook, and an idempotent `create_sap_sales_invoice`.
* Install (Frappe Cloud: add as a private app from a Git repository, deploy to a **staging bench first**): `bench get-app <repo>`, `bench --site <site> install-app sap_b1_direct_integration`, `bench --site <site> migrate`.
* Create a dedicated API user, give it a restricted role and **User Permission -> Company** for each company it may serve.
* Tests: `bench --site <test-site> run-tests --app sap_b1_direct_integration` (throw-away site only).

STATUS: not yet executed on a bench in this repository's development environment.
