app_name = "sap_b1_direct_integration"
app_title = "SAP B1 Direct Integration"
app_publisher = "Integration vendor"
app_description = "Generic direct integration of SAP Business One with ERPNext (invoice sync, E-Invoice, E-Way Bill) - no customer-specific values"
app_email = "support@example.invalid"
app_license = "MIT"
required_apps = ["erpnext", "india_compliance"]

after_install = "sap_b1_direct_integration.install.after_install"

doc_events = {
    "Sales Invoice": {
        "validate": "sap_b1_direct_integration.events.validate_sap_reference",
        "on_cancel": "sap_b1_direct_integration.events.release_reference_on_cancel",
    }
}
