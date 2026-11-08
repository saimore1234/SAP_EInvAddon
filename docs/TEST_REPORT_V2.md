# Test report 2.0.0

## Automated results (this machine)
| Suite | Command | Result |
|---|---|---|
| C# (xUnit, net48) | `dotnet test src/EInvGstAddon.Tests` | **95 passed** (73 carried over from 1.x incl. the legacy Sales-Invoice engine tests + 22 new) |
| Python (unittest, framework independent core + API layer with a fake `frappe`) | `PYTHONPATH=frappe_app/sap_b1_direct_integration python -m unittest discover -s tests -t .` | **61 passed, 1 skipped (bench-only assertion)** |
| In-SAP menu self-test (real SAP B1 10.0 client, UI API) | `EInvGstAddon.exe <conn> --selftest-menu` | **PASS** (22-item right-click menu created under SAP menu 1280, repeated creation, enable flags, removal) |
| Real-data check (read-only) | live test + Python validator on real Vipra invoices | export invoices (EUR/INR) and domestic CGST/IGST invoices build valid contracts and NIC payloads; TCS invoice correctly blocked until mapped; a missing-GSTIN customer correctly rejected |

## Requirement → proof (spec §16)
| # | Requirement | Where proven |
|---|---|---|
| 1 | SAP invoice JSON accepted | `test_valid_invoice_json_is_accepted_and_passes`, `InvoiceJsonBuilderTests`, `GstGatewayTests` |
| 2–6, 20 | No Sales Invoice / Customer / Item / stock / accounting document | `NoSalesInvoiceDependencyTests` (C#), `NoAccountingDocumentsTests` (AST, Python), `GstGatewayTests.Every_operation_uses_only_the_gst_api…`; bench assertion in `test_bench_zero_sales_invoice.py` (**not run: needs a bench**) |
| 7 | Valid invoice passes | `ValidationTests` |
| 8 | Invalid GSTIN rejected (format + check character) | `test_invalid_gstin_is_rejected` |
| 9 | Invalid HSN rejected, never replaced | `test_invalid_hsn_is_rejected_and_never_replaced` |
| 10 | Duplicate detected | `test_duplicate_request_returns_existing…`, `test_duplicate_ewb_is_not_generated_twice` |
| 11 | Concurrent duplicates blocked | `test_concurrent_duplicate_requests_only_one_reaches_the_gsp` (threads, atomic reference creation) |
| 12 | Lost GSP response reconciled | `test_lost_response_is_not_retried_and_is_reconciled…`, EWB lookup variant, vehicle read-back |
| 13/14 | IRN / EWB returned to SAP | `test_generate_returns_the_actual_irn…`, `EWayBillTests`, `GstGatewayTests.The_server_answer_is_returned_exactly…` |
| 15 | Cancellation status-checked | `test_cancellation_…` (success only when the IRP/portal confirms; unconfirmed → Unknown) |
| 16 | Multiple SAP company configurations independent | `test_multiple_sap_company_configurations_are_independent`, `Two_configurations_send_their_own_identity…`, `ConfigStoreTests` |
| 17 | Incorrect company access rejected | `SecurityTests` (user, database, GSTIN, profile, credentials, operation permissions) |
| 18 | Credentials never in logs | `test_audit_log_never_contains_payload_or_credentials`, `test_audit_log_redacts…`, `Api_secret_is_never_written_to_the_log` |
| 19 | Sandbox / production cannot be confused | `test_sandbox_and_production_cannot_be_confused`, `test_production_does_not_substitute_any_value` |

## Verified vs still awaiting real GSP sandbox testing
**Verified (automated or live read-only):** contract building from real SAP data, validation rules, NIC payload structure, idempotency/locking/status machine, reconciliation logic against a faithful fake of the IRP's uniqueness behaviour, access control, log redaction, company/environment isolation, right-click menu creation in a real SAP client, absence of Sales Invoice code paths.
**NOT verified – needs a sandbox ERPNext site + sandbox GSP credentials (none available):**
* the Frappe app has **never been executed on a bench** (no bench here; only its framework-independent core and API layer ran with a fake `frappe`). DocType JSON, `FrappeStore` row locking/commit behaviour, `rate_limit`, hooks and scheduler tasks are unexecuted;
* real calls through `EInvoiceAPI` / `EWaybillAPI` (response shapes of `get_e_waybill`, `get_e_waybills_by_date`, duplicate-IRN `Desc`, sandbox acceptance of the payload);
* **no real IRN or e-Way Bill has been generated** by this release; do not describe GST generation as working until a sandbox run succeeds;
* the SAP dialogs and DI persistence for the new `@EINV_DOCS`/`@EINV_TXN`/UDFs, the right-click event itself, printing, form refresh – compiled, partially self-tested, not exercised end to end in the SAP GUI;
* automatic E-Invoice/E-Way Bill triggers (switches stored and gated; no trigger implemented).
