# Production deployment checklist (2.0)

Do not start until the sandbox run in SANDBOX_TESTING.md has produced real successful IRP/EWB responses.

## ERPNext
- [ ] Frappe/ERPNext/India Compliance versions are 15.x; the app was installed on staging first (`bench migrate` clean, `bench run-tests` green incl. `test_bench_zero_sales_invoice`).
- [ ] Scheduler enabled; Integration Request retention understood (cleanup task active; consider ERPNext Log Settings).
- [ ] GST Settings: **Sandbox Mode OFF**, real GSP credentials for the company GSTIN, e-Invoice / e-Waybill enabled.
- [ ] **SAP Integration Profile** per SAP company: Environment **Production**, correct database / GSTIN / API user; one profile per company, never shared.
- [ ] API user restricted to the integration role; key/secret rotated per CREDENTIAL_ROTATION.md; HTTPS only.

## SAP
- [ ] Release package installed (Install.ps1) and registered in Add-On Administration; configuration created for each SAP company (ERPNext Connection Settings), *Check Integration Profile* green.
- [ ] *Create SAP UDF/UDT* run once per company database; users logged out/in afterwards.
- [ ] GST Configuration validated; tax mapping, UQC map, document number pattern (≤16 chars, unique per financial year) agreed with the accountant; TCS and freight mapping tested on real invoices.
- [ ] Authorised SAP users listed (GST users / admins); automatic generation left OFF until reviewed.

## Go-live
- [ ] First live invoice: Validate → review the confirmation text (it must say PRODUCTION) → Generate → verify IRN on the government portal → verify SAP fields.
- [ ] 24-hour cancellation rule and *Unknown* reconciliation explained to users.
- [ ] Monitoring: Integration History / Error Logs screens, ERPNext *SAP GST Transaction* list filtered on Unknown / Failed.
