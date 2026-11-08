import frappe


def execute():
    """
    Database-level uniqueness of the SAP reference (SAP database + object type + DocEntry [+ configuration id]).
    Empty references are stored as NULL, so every other (non-SAP) Sales Invoice is unaffected.
    Cancelled invoices release their reference (events.release_reference_on_cancel), so a SAP document can be re-synchronised after a cancellation.
    """
    if not frappe.db.has_column("Sales Invoice", "custom_sap_sync_reference"):
        return
    frappe.db.sql("UPDATE `tabSales Invoice` SET custom_sap_sync_reference = NULL WHERE custom_sap_sync_reference = ''")
    dup = frappe.db.sql(
        """SELECT custom_sap_sync_reference, COUNT(*) c FROM `tabSales Invoice`
           WHERE custom_sap_sync_reference IS NOT NULL GROUP BY custom_sap_sync_reference HAVING c > 1""",
        as_dict=True,
    )
    if dup:
        frappe.throw("Duplicate SAP references exist; resolve them before the unique index can be created: " + ", ".join(d.custom_sap_sync_reference for d in dup))
    frappe.db.add_unique("Sales Invoice", ["custom_sap_sync_reference"], constraint_name="unique_sap_sync_reference")
