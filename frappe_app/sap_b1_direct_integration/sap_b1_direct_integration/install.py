from frappe.custom.doctype.custom_field.custom_field import create_custom_fields

REF = {"read_only": 1, "no_copy": 1, "print_hide": 1}


def after_install():
    """Same fields the SAP add-on creates through REST (idempotent), plus the configuration id."""
    create_custom_fields(
        {
            "Sales Invoice": [
                dict(fieldname="custom_sap_database", label="SAP Database", fieldtype="Data", insert_after="company", **REF),
                dict(fieldname="custom_sap_docentry", label="SAP DocEntry", fieldtype="Data", insert_after="custom_sap_database", search_index=1, **REF),
                dict(fieldname="custom_sap_docnum", label="SAP DocNum", fieldtype="Data", insert_after="custom_sap_docentry", **REF),
                dict(fieldname="custom_sap_object_type", label="SAP Object Type", fieldtype="Data", insert_after="custom_sap_docnum", **REF),
                dict(fieldname="custom_sap_config_id", label="SAP Integration Configuration", fieldtype="Data", insert_after="custom_sap_object_type", **REF),
                dict(fieldname="custom_sap_sync_reference", label="SAP Sync Reference", fieldtype="Data", insert_after="custom_sap_config_id", search_index=1, **REF),
            ],
            "Sales Invoice Item": [
                dict(fieldname="custom_sap_line_num", label="SAP Line No", fieldtype="Data", insert_after="item_code", **REF),
                dict(fieldname="custom_sap_tax_code", label="SAP Tax Code", fieldtype="Data", insert_after="custom_sap_line_num", **REF),
            ],
            "Customer": [
                dict(fieldname="custom_sap_database", label="SAP Database", fieldtype="Data", insert_after="customer_name", **REF),
                dict(fieldname="custom_sap_card_code", label="SAP Card Code", fieldtype="Data", insert_after="custom_sap_database", search_index=1, **REF),
            ],
            "Item": [
                dict(fieldname="custom_sap_database", label="SAP Database", fieldtype="Data", insert_after="item_code", **REF),
                dict(fieldname="custom_sap_item_code", label="SAP Item Code", fieldtype="Data", insert_after="custom_sap_database", search_index=1, **REF),
            ],
        }
    )
