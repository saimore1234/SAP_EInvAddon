using System;
using System.Collections.Generic;
using EInvGstAddon.Common;
using Newtonsoft.Json.Linq;

namespace EInvGstAddon.ERPNext
{
    /// <summary>
    /// Creates the SAP reference Custom Fields in ERPNext (idempotent, additive only: existing fields and data are never changed).
    /// </summary>
    public sealed class ErpSchema
    {
        private readonly ErpClient _erp;
        public ErpSchema(ErpClient erp) { _erp = erp; }

        private struct F
        {
            public string Dt, Name, Label, Type, After; public bool Index;
            public F(string dt, string name, string label, string type, string after, bool index = false) { Dt = dt; Name = name; Label = label; Type = type; After = after; Index = index; }
        }

        private static readonly F[] Fields =
        {
            new F("Sales Invoice", "custom_sap_database",       "SAP Database",       "Data", "company"),
            new F("Sales Invoice", "custom_sap_docentry",       "SAP DocEntry",       "Data", "custom_sap_database", true),
            new F("Sales Invoice", "custom_sap_docnum",         "SAP DocNum",         "Data", "custom_sap_docentry"),
            new F("Sales Invoice", "custom_sap_object_type",    "SAP Object Type",    "Data", "custom_sap_docnum"),
            new F("Sales Invoice", "custom_sap_config_id",      "SAP Integration Configuration", "Data", "custom_sap_object_type"),
            new F("Sales Invoice", "custom_sap_sync_reference", "SAP Sync Reference", "Data", "custom_sap_config_id", true),
            new F("Sales Invoice Item", "custom_sap_line_num",  "SAP Line No",        "Data", "item_code"),
            new F("Sales Invoice Item", "custom_sap_tax_code",  "SAP Tax Code",       "Data", "custom_sap_line_num"),
            new F("Customer", "custom_sap_database",            "SAP Database",       "Data", "customer_name"),
            new F("Customer", "custom_sap_card_code",           "SAP Card Code",      "Data", "custom_sap_database", true),
            new F("Item", "custom_sap_database",                "SAP Database",       "Data", "item_code"),
            new F("Item", "custom_sap_item_code",               "SAP Item Code",      "Data", "custom_sap_database", true),
        };

        /// <summary>Creates missing fields; returns the names created.</summary>
        public List<string> Ensure()
        {
            var created = new List<string>();
            foreach (var f in Fields)
            {
                string id = f.Dt + "-" + f.Name;
                if (_erp.Exists("Custom Field", id)) continue;
                var doc = new JObject
                {
                    ["dt"] = f.Dt, ["fieldname"] = f.Name, ["label"] = f.Label, ["fieldtype"] = f.Type,
                    ["insert_after"] = f.After, ["read_only"] = 1, ["no_copy"] = 1, ["print_hide"] = 1, ["search_index"] = f.Index ? 1 : 0
                };
                _erp.Insert("Custom Field", doc);
                created.Add(id);
                Logger.Info("erp.customfield.created", "field", id);
            }
            return created;
        }

        public List<string> Missing()
        {
            var missing = new List<string>();
            foreach (var f in Fields) { string id = f.Dt + "-" + f.Name; if (!_erp.Exists("Custom Field", id)) missing.Add(id); }
            return missing;
        }
    }
}
