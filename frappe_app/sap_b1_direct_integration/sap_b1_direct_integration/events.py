import frappe
from frappe import _


def validate_sap_reference(doc, method=None):
    """Server-side duplicate guard (second layer next to the database unique index)."""
    ref = doc.get("custom_sap_sync_reference")
    if not ref:
        doc.custom_sap_sync_reference = None          # NULLs are allowed many times by the unique index
        return
    existing = frappe.db.get_value(
        "Sales Invoice",
        {"custom_sap_sync_reference": ref, "name": ["!=", doc.name or ""]},
        "name",
    )
    if existing:
        frappe.throw(
            _("SAP document {0} is already synchronised as Sales Invoice {1}").format(ref, existing),
            exc=frappe.DuplicateEntryError,
        )


def release_reference_on_cancel(doc, method=None):
    """A cancelled ERPNext invoice frees its SAP reference so the SAP document can be synchronised again after a genuine cancellation."""
    ref = doc.get("custom_sap_sync_reference")
    if ref:
        doc.db_set("custom_sap_sync_reference", f"{ref}~cancelled~{doc.name}"[:140], update_modified=False)
