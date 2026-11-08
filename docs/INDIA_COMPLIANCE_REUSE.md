# india_compliance 15.29.0 – what can be reused without a Sales Invoice

Source inspected: `india_compliance/gst_india/` of tag v15.29.0.

## Document based (cannot be used without a Sales Invoice – NOT called)
| Function | Why |
|---|---|
| `utils.e_invoice.generate_e_invoice(docname, throw, force)` | `load_doc("Sales Invoice", docname, "submit")`, `EInvoiceData(doc).get_data()` |
| `utils.e_invoice.cancel_e_invoice(docname, values)` | loads the document, calls `doc.cancel()` |
| `utils.e_waybill.generate_e_waybill(*, doctype, docname, values, force)` and the cancel / update / fetch functions | `load_doc(doctype, docname)`, `EWaybillData(doc)`, document fields |
| `EInvoiceData`, `EWaybillData`, `log_and_process_*` | build/store everything from the document |

## Document free (reused)
| Component | Location | Used for |
|---|---|---|
| `EInvoiceAPI.create(company_gstin=...)` → `StandardEInvoiceAPI` / `EnrichedEInvoiceAPI` | `api_classes/nic/e_invoice.py` | `generate_irn(data)`, `cancel_irn(data)`, `get_e_invoice_by_irn(irn)`, `generate_e_waybill(data)` (with IRN), `get_e_waybill_by_irn(irn)` – `setup(doc=None, *, company_gstin=None)` explicitly supports a bare GSTIN |
| `EWaybillAPI.create(company_gstin=...)` | `api_classes/nic/e_waybill.py` | `generate_e_waybill`, `get_e_waybill`, `get_e_waybills_by_date`, `cancel_e_waybill`, `update_vehicle_info`, `update_transporter` |
| `BaseAPI` / `nic/auth.py` | `api_classes/` | GSP authentication, token + session-key handling and refresh, request encryption/decryption, sandbox routing, credential lookup by GSTIN and service in GST Settings, error classification (`GatewayTimeoutError`, `GSPServerError`, ...) |
| `utils/cryptography.py` | | AES / HMAC helpers used by the above |

## Built by this app (because it exists only inside the document classes)
NIC e-Invoice schema 1.1 payload, e-Way Bill payloads (with and without IRN), cancel/vehicle/transporter payloads → `core/nic_payload.py`. Field names and structure follow `EInvoiceData.get_invoice_data()` / `EWaybillData.get_transaction_data()` of 15.29.0. GST validation → `core/validation.py`.

## Verified constants
Cancel reasons (e-Invoice: Duplicate 1, Data Entry Mistake 2, Order Cancelled 3, Others 4; e-Waybill: Duplicate 1, Order Cancelled 2, Data Entry Mistake 3, Others 4), vehicle update reasons (Break Down 1, Trans Shipment 2, Others 3, First Time 4), `IGNORED_ERROR_CODES` (2150 duplicate IRN, 2283 details older than 2 days, 9999 already cancelled, 4002 e-Waybill exists for IRN, 604 / 312 / 238 for e-Waybill) – see `core/constants.py`, `core/gsp.py`.

## Behaviours to know
* **Sandbox**: india_compliance replaces seller/buyer identities and the document number with registered test values. The app does the same *explicitly* (response field `sandbox_substitutions`) and only after validating the real data.
* **Duplicate IRN (2150)**: the IRP answers a repeated generate with the existing IRN; the adapter verifies buyer GSTIN and total against the signed document before reusing it (same check as `verify_e_invoice_details`). This is also how an uncertain generation is reconciled (identical data, same request hash).
* **Integration Request logging**: every GSP call is logged by india_compliance as an *Integration Request* including the request payload. The app cannot suppress this without patching another app. Mitigation: scheduled cleanup after 30 days (`tasks.cleanup_integration_requests`); optionally set a shorter retention in ERPNext Log Settings.
* **IRN details** can be fetched from the IRP only for ~2 days after generation (error 2283). SAP therefore stores the signed QR / signed invoice (memo fields of `@EINV_DOCS`) at generation time; ERPNext stores neither.
* `check_scheduler_status()` runs in the API setup: the site's scheduler must be enabled.
