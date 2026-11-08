import frappe
from frappe import _


def allowed_companies():
    """
    Companies the API user may use, taken from ERPNext's own User Permissions (Company).
    A user without any Company permission gets NOTHING (deny by default), so a request parameter can never widen access.
    System-wide administrators are not special-cased: restrict the API user explicitly.
    """
    return frappe.get_all(
        "User Permission",
        filters={"user": frappe.session.user, "allow": "Company"},
        pluck="for_value",
    )


def assert_company_access(company):
    if not company:
        frappe.throw(_("Company is required"), exc=frappe.PermissionError)
    if company not in allowed_companies():
        frappe.throw(
            _("User {0} is not permitted to use company {1} through the SAP integration").format(frappe.session.user, company),
            exc=frappe.PermissionError,
        )


def assert_gst_ready(company):
    """GST actions require a GSTIN on the company and GST credentials for it in GST Settings (checked again by india_compliance)."""
    if not frappe.db.get_value("Company", company, "gstin"):
        frappe.throw(_("Company {0} has no GSTIN; E-Invoice / E-Waybill are not available").format(company))
    settings = frappe.get_cached_doc("GST Settings")
    if not any(c.company == company for c in settings.get("credentials") or []):
        frappe.throw(_("No GSP credentials are configured for company {0} in GST Settings").format(company))


def log(action, doc, status, message="", reference=None):
    try:
        frappe.get_doc(
            {
                "doctype": "SAP Sync Log",
                "action": action,
                "status": status,
                "message": (message or "")[:140],
                "company": doc.get("company"),
                "sap_database": doc.get("custom_sap_database"),
                "object_type": doc.get("custom_sap_object_type"),
                "docentry": doc.get("custom_sap_docentry"),
                "reference": reference,
            }
        ).insert(ignore_permissions=True)
    except Exception:
        frappe.log_error(title="SAP Sync Log failed")
