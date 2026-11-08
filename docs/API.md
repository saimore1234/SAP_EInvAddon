# GST API (Frappe app `sap_b1_direct_integration` 2.0)

Base: `POST https://<erpnext>/api/method/sap_b1_direct_integration.api.gst.<endpoint>`
Auth: `Authorization: token API_KEY:API_SECRET` (SAP → ERPNext). GSP credentials stay in ERPNext GST Settings and are never exposed.
Body: JSON. Response: Frappe `{"message": {...}}` containing

```json
{"ok": true, "status": "Generated", "request_id": "…", "reference": "IT1_DB_ONE_13_2624_EINVOICE",
 "message": "…", "data": {…}, "errors": [{"severity":"error","code":"HSN_FORMAT","field":"items[1].hsn_code","message":"…"}], "warnings": [...]}
```
Business outcomes are HTTP 200 with `ok:false`; HTTP 403 only for authentication/authorisation failures.

## Common request fields
`integration_id` (profile), `sap_database`, `object_type` ("13"), `doc_entry` (int, the identity of the document), `environment` (must equal the profile), `request_id` (optional 8–64 chars; generated if absent; echoed in the audit log), and for generation/validation `invoice` (the contract below).

## Endpoints
| Endpoint | Purpose | Notes |
|---|---|---|
| `validate_invoice` | GST validation only | nothing is sent to the IRP, nothing stored |
| `generate_e_invoice` | validate → lock → IRP → IRN | idempotent; returns `data.irn, ack_no, ack_date, signed_invoice, signed_qr` |
| `get_e_invoice_status` | stored state (+ fresh IRP read) | with `invoice` supplied and status Unknown → controlled reconciliation |
| `cancel_e_invoice` | `reason`, `remark` | ≤ 24 h; reported cancelled only after the IRP shows CNL |
| `generate_e_waybill` | `invoice` incl. `transport` | IRN route when an IRN exists, standalone otherwise |
| `get_e_waybill_status` | stored state (+ portal read) | Unknown → looked up by document number/date |
| `cancel_e_waybill` | `reason`, `remark` | ≤ 24 h; confirmed by reading the e-Way Bill back |
| `update_vehicle` | `vehicle_number, reason, remark, place, state_code` | idempotent per request hash |
| `update_transporter` | `transporter_id` | |
| `ping` (GET/POST) | versions, GST sandbox flag, profile consistency | no secrets |

## Invoice contract (excerpt; amounts in INR, original SAP values)
`integration_id, sap_database, object_type, doc_entry, doc_num, document_type (INV|CRN|DBN), document_number, document_date, document_due_date, currency, exchange_rate, environment, supply_type (B2B|SEZWP|SEZWOP|EXPWP|EXPWOP|DEXP), reverse_charge, place_of_supply, seller{gstin, legal_name, trade_name, address, address2, city, state_code, pincode}, buyer{…}, ship_to{…}, dispatch_from{…}, items[{line, item_code, description, hsn_code, quantity, unit (UQC), unit_price, discount, taxable_value, cgst_rate, sgst_rate, igst_rate, cess_rate, cgst_amount, sgst_amount, igst_amount, cess_amount}], tax_totals{taxable_value, cgst, sgst, igst, cess, discount, other_charges, round_off, invoice_total, invoice_total_fc}, transport{transporter_id, transporter_name, vehicle_number, vehicle_type, transport_mode, distance, doc_number, doc_date}, export{country_code, port_code, shipping_bill_no, shipping_bill_date, foreign_currency}, original_document_number/date (CRN/DBN)`.
Validation (never auto-corrected): GSTIN format + check character (sandbox: format only), state/GSTIN agreement, PIN, doc number (≤16, no leading 0), date, HSN 4–8 digits and profile minimum, UQC list, rates, IGST vs CGST/SGST exclusivity, tax amounts vs taxable×rate (₹ tolerance), totals reconciliation, export/SEZ/foreign-currency rules, transport (mode, vehicle, transporter GSTIN, distance 0–4000, LR for rail/air/ship).

## Status model and storage
Statuses: Pending · Validating · Validation Failed · Processing · Generated · Failed · **Unknown / Reconciliation Required** · Cancelled.
Unique reference `<integration>_<database>_<objtype>_<docentry>_<OPERATION>` (vehicle/transporter updates append the first 10 hex of the request hash) is the primary key of **SAP GST Transaction**; creating it is the lock. Stored: reference, operation, status, request hash, request id, lock expiry, IRN, ack no/date, e-Way Bill no/date/validity, error code/message, SAP identifiers. **Not stored:** invoice lines, parties, amounts, payload, signed data.
Uncertain results (timeout/gateway) → `Unknown`, never `Failed`, never retried automatically.

## Security
Token auth; profile bound to one API user, one SAP database, one seller GSTIN and one environment; environment must match GST Settings `sandbox_mode`; operation switches per profile; rate limit per integration; audit log lines contain ids/status only (no payload, GSTIN list or secret); unexpected errors return a generic message and are logged without the request body.
