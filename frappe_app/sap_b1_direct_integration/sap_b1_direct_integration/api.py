"""
Whitelisted API of the generic SAP B1 <-> ERPNext integration app.

Design rules
* No GST logic is implemented here: E-Invoice / E-Way Bill operations call the methods of the GST app installed on the site
  (india_compliance.gst_india.utils.e_invoice / e_waybill; signatures verified in v15.29.0).
* Company access is enforced on EVERY request on the server side (see utils.assert_company_access); a request parameter can never widen it.
* Nothing is specific to one customer: SAP database, configuration id and company arrive per request and are validated.

STATUS: written against frappe 15 / india_compliance 15.29.0 sources; NOT yet executed on a bench (no bench was available).
Run sap_b1_direct_integration/tests on a throw-away site before installing on a production site.
"""

import json

import frappe
from frappe import _

from sap_b1_direct_integration import utils

E_INVOICE = "india_compliance.gst_india.utils.e_invoice"
E_WAYBILL = "india_compliance.gst_india.utils.e_waybill"


# --------------------------------------------------------------------------- connection / discovery
@frappe.whitelist(methods=["GET", "POST"])
def test_connection():
    versions = frappe.get_attr("frappe.utils.change_log.get_versions")()
    return {
        "user": frappe.session.user,
        "frappe": versions.get("frappe", {}).get("version"),
        "erpnext": versions.get("erpnext", {}).get("version"),
        "india_compliance": versions.get("india_compliance", {}).get("version"),
        "app": "sap_b1_direct_integration",
        "companies": utils.allowed_companies(),
    }


@frappe.whitelist(methods=["GET", "POST"])
def get_company_list():
    return frappe.get_all(
        "Company",
        filters={"name": ["in", utils.allowed_companies() or ["__none__"]]},
        fields=["name", "abbr", "default_currency", "gstin", "country"],
    )


@frappe.whitelist(methods=["GET", "POST"])
def get_warehouse_list(company: str):
    utils.assert_company_access(company)
    return frappe.get_all("Warehouse", filters={"company": company, "is_group": 0, "disabled": 0}, fields=["name", "warehouse_name"])


@frappe.whitelist(methods=["GET", "POST"])
def get_account_list(company: str, account_type: str | None = None):
    utils.assert_company_access(company)
    filters = {"company": company, "is_group": 0, "disabled": 0}
    if account_type:
        filters["account_type"] = account_type
    return frappe.get_all("Account", filters=filters, fields=["name", "account_type", "tax_rate"])


@frappe.whitelist(methods=["GET", "POST"])
def get_customer_mapping(sap_database: str, card_code: str | None = None):
    filters = {"custom_sap_database": sap_database}
    if card_code:
        filters["custom_sap_card_code"] = card_code
    return frappe.get_all("Customer", filters=filters, fields=["name", "customer_name", "custom_sap_card_code"], limit_page_length=0)


@frappe.whitelist(methods=["GET", "POST"])
def get_item_mapping(sap_database: str, item_code: str | None = None):
    filters = {"custom_sap_database": sap_database}
    if item_code:
        filters["custom_sap_item_code"] = item_code
    return frappe.get_all("Item", filters=filters, fields=["name", "item_name", "custom_sap_item_code", "gst_hsn_code"], limit_page_length=0)


# --------------------------------------------------------------------------- invoice creation (idempotent)
def _reference(sap_database, object_type, docentry, config_id=None):
    ref = f"{sap_database}-{object_type}-{docentry}"
    return f"{ref}-{config_id}" if config_id else ref


@frappe.whitelist(methods=["GET", "POST"])
def get_invoice_by_sap_reference(sap_database: str, object_type: str, docentry: str, config_id: str | None = None):
    ref = _reference(sap_database, object_type, docentry, config_id)
    row = frappe.db.get_value(
        "Sales Invoice",
        {"custom_sap_sync_reference": ref},
        ["name", "company", "docstatus", "customer", "grand_total", "creation"],
        as_dict=True,
    )
    if row:
        utils.assert_company_access(row.company)
    return row


@frappe.whitelist(methods=["POST"])
def create_sap_sales_invoice(payload: str | dict):
    """Insert a Draft Sales Invoice for a SAP document. Repeating the request returns the existing document (never a second one)."""
    doc = frappe.parse_json(payload)
    utils.assert_company_access(doc.get("company"))
    for key in ("custom_sap_database", "custom_sap_object_type", "custom_sap_docentry", "custom_sap_sync_reference"):
        if not doc.get(key):
            frappe.throw(_("{0} is required").format(key), exc=frappe.ValidationError)
    ref = doc["custom_sap_sync_reference"]

    existing = frappe.db.get_value("Sales Invoice", {"custom_sap_sync_reference": ref}, ["name", "company", "docstatus"], as_dict=True)
    if existing:
        utils.assert_company_access(existing.company)
        utils.log("create_sap_sales_invoice", doc, "Duplicate", _("Existing invoice returned"), existing.name)
        return {"name": existing.name, "created": False, "docstatus": existing.docstatus}

    doc["doctype"] = "Sales Invoice"
    doc["docstatus"] = 0                      # Phase 1/2 contract: never submit from this API
    try:
        si = frappe.get_doc(doc).insert()
    except frappe.DuplicateEntryError:        # lost the race against the unique index: return the winner
        frappe.db.rollback()
        name = frappe.db.get_value("Sales Invoice", {"custom_sap_sync_reference": ref})
        utils.log("create_sap_sales_invoice", doc, "Duplicate", _("Concurrent duplicate resolved"), name)
        return {"name": name, "created": False, "docstatus": 0}
    utils.log("create_sap_sales_invoice", doc, "Success", "", si.name)
    return {"name": si.name, "created": True, "docstatus": si.docstatus}


# --------------------------------------------------------------------------- E-Invoice (delegates to india_compliance)
def _load_invoice(docname):
    doc = frappe.get_doc("Sales Invoice", docname)
    utils.assert_company_access(doc.company)
    return doc


@frappe.whitelist(methods=["POST"])
def generate_einvoice(docname: str, force: bool = False):
    doc = _load_invoice(docname)
    utils.assert_gst_ready(doc.company)
    if doc.docstatus != 1:
        frappe.throw(_("Submit the Sales Invoice before generating the e-Invoice."))
    if doc.irn:
        return get_einvoice_status(docname)            # never regenerate
    frappe.get_attr(f"{E_INVOICE}.generate_e_invoice")(docname, throw=True, force=bool(force))
    utils.log("generate_einvoice", doc, "Success", "", docname)
    return get_einvoice_status(docname)


@frappe.whitelist(methods=["GET", "POST"])
def get_einvoice_status(docname: str):
    doc = _load_invoice(docname)
    log = frappe.get_all(
        "e-Invoice Log",
        filters={"reference_doctype": "Sales Invoice", "reference_name": docname},
        fields=["name", "acknowledgement_number", "acknowledged_on", "signed_qr_code", "is_cancelled", "cancelled_on"],
        order_by="creation desc",
        limit=1,
    )
    return {"name": docname, "company": doc.company, "docstatus": doc.docstatus, "irn": doc.irn, "einvoice_status": doc.get("einvoice_status"), "log": log[0] if log else None}


@frappe.whitelist(methods=["POST"])
def cancel_einvoice(docname: str, reason: str, remark: str | None = None):
    doc = _load_invoice(docname)
    utils.assert_gst_ready(doc.company)
    frappe.get_attr(f"{E_INVOICE}.cancel_e_invoice")(docname, {"reason": reason, "remark": remark or reason})
    utils.log("cancel_einvoice", doc, "Success", reason, docname)
    return get_einvoice_status(docname)


# --------------------------------------------------------------------------- E-Way Bill (delegates to india_compliance)
@frappe.whitelist(methods=["POST"])
def generate_ewaybill(docname: str, values: str | dict | None = None, force: bool = False):
    doc = _load_invoice(docname)
    utils.assert_gst_ready(doc.company)
    if doc.docstatus != 1:
        frappe.throw(_("Submit the Sales Invoice before generating the e-Waybill."))
    if doc.ewaybill:
        return get_ewaybill_status(docname)
    frappe.get_attr(f"{E_WAYBILL}.generate_e_waybill")(doctype="Sales Invoice", docname=docname, values=values, force=bool(force))
    utils.log("generate_ewaybill", doc, "Success", "", docname)
    return get_ewaybill_status(docname)


@frappe.whitelist(methods=["GET", "POST"])
def get_ewaybill_status(docname: str):
    doc = _load_invoice(docname)
    log = frappe.get_all(
        "e-Waybill Log",
        filters={"reference_doctype": "Sales Invoice", "reference_name": docname},
        fields=["name", "e_waybill_number", "valid_upto", "created_on", "is_cancelled"],
        order_by="creation desc",
        limit=1,
    )
    return {
        "name": docname, "company": doc.company, "ewaybill": doc.ewaybill, "vehicle_no": doc.get("vehicle_no"),
        "gst_transporter_id": doc.get("gst_transporter_id"), "log": log[0] if log else None,
    }


@frappe.whitelist(methods=["POST"])
def cancel_ewaybill(docname: str, reason: str, remark: str | None = None):
    doc = _load_invoice(docname)
    utils.assert_gst_ready(doc.company)
    frappe.get_attr(f"{E_WAYBILL}.cancel_e_waybill")(doctype="Sales Invoice", docname=docname, values={"reason": reason, "remark": remark or reason})
    utils.log("cancel_ewaybill", doc, "Success", reason, docname)
    return get_ewaybill_status(docname)


@frappe.whitelist(methods=["POST"])
def update_vehicle(docname: str, values: str | dict):
    doc = _load_invoice(docname)
    utils.assert_gst_ready(doc.company)
    frappe.get_attr(f"{E_WAYBILL}.update_vehicle_info")(doctype="Sales Invoice", docname=docname, values=values)
    return get_ewaybill_status(docname)


@frappe.whitelist(methods=["POST"])
def update_transporter(docname: str, values: str | dict):
    doc = _load_invoice(docname)
    utils.assert_gst_ready(doc.company)
    frappe.get_attr(f"{E_WAYBILL}.update_transporter")(doctype="Sales Invoice", docname=docname, values=values)
    return get_ewaybill_status(docname)


@frappe.whitelist(methods=["GET", "POST"])
def get_generated_document_details(docname: str):
    """IRN + e-Waybill details of one invoice in a single call (signed payloads included)."""
    doc = _load_invoice(docname)
    inv_log = frappe.get_all(
        "e-Invoice Log",
        filters={"reference_doctype": "Sales Invoice", "reference_name": docname},
        fields=["name", "acknowledgement_number", "acknowledged_on", "signed_qr_code", "signed_invoice", "is_cancelled"],
        order_by="creation desc", limit=1,
    )
    return {"company": doc.company, "irn": doc.irn, "einvoice_log": inv_log[0] if inv_log else None, "ewaybill": get_ewaybill_status(docname)}


@frappe.whitelist(methods=["GET", "POST"])
def get_sync_logs(sap_database: str | None = None, limit: int = 100):
    companies = utils.allowed_companies() or ["__none__"]
    filters = {"company": ["in", companies]}
    if sap_database:
        filters["sap_database"] = sap_database
    return frappe.get_all("SAP Sync Log", filters=filters, fields=["*"], order_by="creation desc", limit=int(limit))
