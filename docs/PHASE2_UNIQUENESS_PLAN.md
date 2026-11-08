# Phase 2 plan – database-level duplicate protection (Frappe app `sap_b1_direct_integration`)

Today: the add-on queries `custom_sap_sync_reference` before every insert (client-side, idempotent). A concurrent or manual second insert is not blocked by ERPNext itself. Phase 2 adds a server-side guarantee.

## Design
* **Frappe custom app** `sap_b1_direct_integration` (own repo, own solution, deployed to the site through the Frappe Cloud dashboard as a private app – it cannot be pushed by REST).
* **Unique key** = `custom_sap_database` + `custom_sap_object_type` + `custom_sap_docentry` (the existing `custom_sap_sync_reference` = `<db>-<objtype>-<docentry>` is derived from these three).
* **Enforcement (two layers)**
  1. `Sales Invoice.before_insert` / `validate` doc-event hook: if any other non-cancelled Sales Invoice has the same key → `frappe.throw(DuplicateEntryError)`; runs inside the request so it holds for any API client or manual user.
  2. A real DB constraint: patch adds a MariaDB unique index on `tabSales Invoice (custom_sap_sync_reference)` with empty values stored as NULL (multiple NULLs are allowed, so TTCPL invoices are unaffected). Cancelled invoices: reference is cleared on cancel (hook `on_cancel`) so SAP can re-sync after a genuine cancellation.
* **Whitelisted methods** (all prefixed `sap_b1_direct_integration.api.`; only wrappers, no GST logic duplicated): `get_invoice_by_sap_reference(db, objtype, docentry)`, `create_sap_sales_invoice(payload)` (insert + uniqueness in one transaction, returns existing on conflict), `test_connection()`. Phase 3 methods call `india_compliance.gst_india.utils.e_invoice.generate_e_invoice` / `e_waybill.generate_e_waybill` (verified signatures in 15.29.0).
* **Safety**: every method checks `company == 'Vipra Closures Private Limited'` (configurable list); refuses TTCPL; role-restricted to an API user with a dedicated role.
* **Tests**: unit tests with `bench run-tests` on a throw-away bench (never on the shared site): duplicate insert rejected, NULL references allowed, cancel then re-insert allowed, TTCPL invoice unaffected.
* **Rollout**: install on a staging Frappe Cloud bench first; migration is additive (index + hooks); rollback = remove app + drop index.
* **Add-on change**: `InvoiceSync` switches from `POST /api/resource/Sales Invoice` to `create_sap_sales_invoice`; DuplicateEntry response → treated as "found existing".
