# Compatibility guide

## Verified baseline
| Component | Version |
|---|---|
| SAP Business One | 10.00.320 x64 (UI API + DI API 10.0) |
| .NET Framework | 4.8 |
| Frappe / ERPNext | 15.108.0 / 15.108.3 |
| india_compliance | 15.29.0 |

## GST methods used (read from the installed v15.29.0 source)
| Operation | Method (module `india_compliance.gst_india.utils.`) | Notes |
|---|---|---|
| Generate IRN | `e_invoice.generate_e_invoice(docname, throw=True, force=False)` | needs a **submitted** Sales Invoice (`load_doc(..., "submit")`); refuses if `irn` exists |
| Cancel IRN | `e_invoice.cancel_e_invoice(docname, values)` `values = {reason, remark}` | reasons: Duplicate, Order Cancelled, Data Entry Mistake, Others; only within 24 h of acknowledgement; **also cancels the ERPNext Sales Invoice** and an active e-Waybill |
| Generate EWB | `e_waybill.generate_e_waybill(*, doctype, docname, values=None, force=False)` | keyword-only; `values`: transporter, gst_transporter_id, vehicle_no, distance, lr_no, lr_date, mode_of_transport, gst_vehicle_type, port_address |
| Cancel EWB | `e_waybill.cancel_e_waybill(*, doctype, docname, values)` | `values = {reason, remark}` |
| Update vehicle | `e_waybill.update_vehicle_info(*, doctype, docname, values)` | `values` + place_of_change, state, reason (Due to Break Down / Due to Trans Shipment / First Time / Others), remark, update_e_waybill_data |
| Update transporter | `e_waybill.update_transporter(*, doctype, docname, values)` | `gst_transporter_id`, `update_e_waybill_data` |
| Fetch EWB | `e_waybill.fetch_e_waybill_data(*, doctype, docname, attach=False, force=False)` | |

Status is read from `Sales Invoice` (`irn`, `einvoice_status`, `ewaybill`, `vehicle_no`, `gst_transporter_id`, ...), `e-Invoice Log`
(`acknowledgement_number`, `acknowledged_on`, `signed_qr_code`, `signed_invoice`, `is_cancelled`) and `e-Waybill Log`
(`e_waybill_number`, `created_on`, `valid_upto`, `is_cancelled`).

## Runtime checks (`GstService.CheckVersions` / `ValidateGstConfiguration`)
* India Compliance not installed → GST screens report it; invoice synchronisation is unaffected.
* Frappe or India Compliance major version ≠ 15 → GST actions disabled with an explanatory message.
* India Compliance 15.x other than 15.29.0 → warning, same API assumed – **test in a sandbox first**.
* Company without GSTIN, no GSP credentials for the company, `enable_api` off → blocked before any call.
* **Sandbox/production consistency**: ERPNext GST Settings `sandbox_mode` is global for the site. A configuration marked *Sandbox* is blocked when the site is in production mode (a real IRN would be created).

## Other versions
ERPNext 14 and 16 are not supported (field and method names were not inspected). To support a new GST app version: read its
`e_invoice.py` / `e_waybill.py`, compare with the table above, update `GstService` constants and tests.
