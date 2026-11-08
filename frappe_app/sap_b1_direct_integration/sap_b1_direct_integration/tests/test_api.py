import frappe
from frappe.tests.utils import FrappeTestCase

from sap_b1_direct_integration import api, utils


class TestSapIntegration(FrappeTestCase):
    """Run on a throw-away site with two companies A and B; API user restricted to company A through User Permission."""

    def test_company_access_denied_without_user_permission(self):
        frappe.set_user("Guest")
        self.assertEqual(utils.allowed_companies(), [])
        with self.assertRaises(frappe.PermissionError):
            utils.assert_company_access("Any Company")

    def test_reference_format(self):
        self.assertEqual(api._reference("ABC", "13", "5", "c1"), "ABC-13-5-c1")
        self.assertEqual(api._reference("ABC", "13", "5"), "ABC-13-5")

    # TODO (needs a configured test site): duplicate insert rejected by the unique index, NULL references allowed,
    # cancel releases the reference, requests for company B refused for the company-A user, generate_einvoice refuses Draft invoices.
