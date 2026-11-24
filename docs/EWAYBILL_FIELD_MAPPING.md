# E-Way Bill field mapping (SAP Business One 10.00.320 -> GST Portal API)

Verified against the live SAP database schema (table and column names read from `INFORMATION_SCHEMA`) and a real A/R Invoice row.
The add-on **reads only**; it never writes or defaults a SAP value. A/R Invoice (object 13) is the only document type the add-on supports.

SAP stores the Electronic Documents > E-Way Bill form of an A/R Invoice in table **`INV26`**, one row per invoice, keyed by `DocEntry`
(`RIN26` is the credit-memo equivalent; credit memos are not supported by the add-on). The data is written by SAP when the invoice is
saved/updated, so after typing it the user must save (Update) the invoice before running the add-on action.

## Transport fields (read from `INV26`, no second entry screen)

| GST Portal API field (`transport.*`) | SAP source | Decoding / rule | Status |
|---|---|---|---|
| `transporter_id` (GSTIN) | `INV26.TransID` | `000000000000000` (SAP placeholder for a transporter without GSTIN) is treated as empty | Verified |
| `transporter_name` | `INV26.TransName` | as is | Verified |
| `transport_mode` | `INV26.TransMode` | 1 Road, 2 Rail, 3 Air, 4 Ship; NULL = empty (reported, never defaulted) | Verified |
| `vehicle_type` | `INV26.VehicleTyp` | `R` Regular, `O` ODC; NULL = empty (reported when a vehicle number is given) | Verified |
| `vehicle_number` | `INV26.VehicleNo` | spaces removed, upper case | Verified |
| `distance` | `INV26.Distance` | km; 0 = portal auto-calculates; valid 0..4000 | Verified |
| `doc_number` (transport document) | `INV26.TransDocNo` | required for Rail/Air/Ship | Verified |
| `doc_date` (transport document) | `INV26.TransDate` | required for Rail/Air/Ship; sent as yyyy-MM-dd | Verified |
| (transporter master) | `INV26.TspEntry` -> `OTSP` (`TransCode`, `TransName`, `TransID`) | not needed: `INV26.TransName/TransID` already hold the chosen transporter | Not used |

Missing mandatory values stop the action with a message naming the SAP field, e.g.
`E-Way Bill cannot be generated. Please enter the Vehicle Number (or the Transporter ID) in the SAP Business One Electronic Documents section (E-Way Bill) of the A/R Invoice.`

## Invoice, party and item fields (unchanged, read by the existing E-Invoice reader)

| GST Portal API field | SAP source | Status |
|---|---|---|
| document number / date / type | `OINV` (`DocNum`/`Series`+`NNM1.SeriesName`, `DocDate`, `ObjType`, `GSTTranTyp`) | Verified (E-Invoice live) |
| Supplier GSTIN, name, address, PIN, state | add-on configuration (company GST address) | Verified; must equal SAP company data |
| Recipient GSTIN | `CRD1.GSTRegnNo` of `OINV.PayToCode` | Verified |
| Recipient address, PIN, state | `CRD1` (Address, Street, City, ZipCode, State) | Verified |
| Ship To | `CRD1` of `OINV.ShipToCode` (when different from bill-to) | Verified |
| Dispatch From | company GST address in the add-on configuration | Verified |
| HSN / SAC | `INV1.HsnEntry` -> `OCHP.ChapterID`, `INV1.SacEntry` -> `OSAC.ServCode` | Verified |
| quantity, UOM, taxable value, rate, tax | `INV1`, `INV4` (`staType`, `TaxRate`, `TaxSum`, `BaseSum`) | Verified |
| IRN (IRN route) | the GST Portal record created by the E-Invoice generation | Verified |

## Standard SAP fields that are NOT used

`INV26` also carries SAP's own copy of the addresses and states (`FrmGSTN`, `FrmState`, `FrmPlace`, `FrmAddres1/2`, `FrmZipCode`, `ActFrmStat`,
`ToGSTN`, `ToState`, `ToPlace`, `ToAddres1/2`, `ToZipCode`, `ActToState`, `SuplyType`, `SubSplyTyp`, `DocType`, `TransType`) and SAP's own result fields
(`EWayBillNo`, `EwbDate`, `ExpireDate`). The add-on does **not** use them for the payload:

* The Bill From / Dispatch From values in the TTC database are still the previous company's data (GSTIN `27AAACO0509P1ZB`, Thane), so using them would
  send a wrong supplier. The add-on sends the supplier and dispatch-from from its own verified configuration and the customer addresses from `CRD1`.
* `EWayBillNo`/`EwbDate`/`ExpireDate` belong to SAP's own e-document process, which is separate from the GST Portal integration. The add-on stores its
  results in its own UDFs/UDT (`@EINV_DOCS`, `U_EWBNo` ...).

If the company should use SAP's Bill From / Dispatch From, they must first be corrected in SAP (Administration > Setup > ... company details / GST).
