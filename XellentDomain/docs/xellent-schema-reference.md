# Xellent EF schema reference

Generated from C# entity classes and `DbContext` configuration by
`python tools/generate-xellent-schema-reference.py`. No database queries are made.
Coverage: **53 distinct mapped tables**: 40 in XellentDomain's
`XellentDbContext` and 13 additional tables in CustomerSupport's
`XellentDatabaseModelContext`. This is the union of those code models, not an inventory
of the entire SQL Server database. See [the model guide](xellent-model-guide.md) for
relationships, scope, and EF investigation examples.

`X` = mapped by XellentDomain; `CS` = mapped by CustomerSupport. Column names
without `[Column]` are derived by EF convention and marked `convention`.
The listed key is the effective EF model key, which may differ from `[Key]` attributes
and is not proof of a SQL Server primary key. Types describe C# mappings, with an
explicit SQL type or string length where the class provides one.
A `?` records C# nullable syntax; it is not a live check of database nullability.
Type facets configured only through fluent calls (for example HasPrecision) are
not extracted here. See [field semantics](xellent-field-semantics.md) for those
differences and the important codes, units, dates, and history rules.

| SQL table | EF models | Columns in union |
|---|---|---:|
| [`ADDRESS`](#address) | X, CS | 11 |
| [`ADDRESSZIPCODE`](#addresszipcode) | X, CS | 6 |
| [`CONTACTPERSON`](#contactperson) | X | 5 |
| [`CUSTINVOICEBACKORDERLINE`](#custinvoicebackorderline) | CS | 13 |
| [`CUSTINVOICEJOUR`](#custinvoicejour) | X, CS | 14 |
| [`CUSTINVOICELINE`](#custinvoiceline) | CS | 13 |
| [`CUSTINVOICESALESLINK`](#custinvoicesaleslink) | CS | 12 |
| [`CUSTINVOICETABLE`](#custinvoicetable) | CS | 18 |
| [`CUSTINVOICETRANS`](#custinvoicetrans) | CS | 21 |
| [`CUSTTABLE`](#custtable) | X, CS | 26 |
| [`CUSTTRANS`](#custtrans) | CS | 13 |
| [`DIRECOMMUNICATIONADDRESS`](#direcommunicationaddress) | X | 6 |
| [`DIRPARTYADDRESSRELATIONSHI1066`](#dirpartyaddressrelationshi1066) | X, CS | 4 |
| [`DIRPARTYADDRESSRELATIONSHIP`](#dirpartyaddressrelationship) | X, CS | 5 |
| [`DIRPARTYECOMMUNICATIONRELA2608`](#dirpartyecommunicationrela2608) | X | 6 |
| [`DOCUREF`](#docuref) | X, CS | 15 |
| [`DOCUTYPE`](#docutype) | CS | 3 |
| [`DOCUVALUE`](#docuvalue) | CS | 4 |
| [`EGS_METERPOINTTYPECODEPERIOD`](#egs_meterpointtypecodeperiod) | X, CS | 9 |
| [`EMS_TIMESERIES`](#ems_timeseries) | X, CS | 8 |
| [`EMS_TIMESERIESVALUES`](#ems_timeseriesvalues) | X, CS | 7 |
| [`EXU_AGREEMENTTABLE`](#exu_agreementtable) | X, CS | 20 |
| [`EXU_BILLINGMETHODPERIOD`](#exu_billingmethodperiod) | X, CS | 9 |
| [`EXU_BILLINGPLAN`](#exu_billingplan) | CS | 3 |
| [`EXU_CONTRACTPARTLOG`](#exu_contractpartlog) | X | 14 |
| [`EXU_CONTRACTPARTTABLE`](#exu_contractparttable) | X, CS | 13 |
| [`EXU_CONTRACTTABLE`](#exu_contracttable) | X, CS | 24 |
| [`EXU_CONTRACTTEMPLATE`](#exu_contracttemplate) | CS | 3 |
| [`EXU_DELPOINT`](#exu_delpoint) | X, CS | 25 |
| [`EXU_DELPOINT2DELPOINTRELA25505`](#exu_delpoint2delpointrela25505) | X, CS | 13 |
| [`EXU_DELPOINTCONTACTINFOTABLE`](#exu_delpointcontactinfotable) | X, CS | 19 |
| [`EXU_DELPOINTPRICEELEMENTR25046`](#exu_delpointpriceelementr25046) | X, CS | 12 |
| [`EXU_FLEXBHISTORYLINE`](#exu_flexbhistoryline) | X | 7 |
| [`EXU_FLEXBHISTORYTABLE`](#exu_flexbhistorytable) | X | 8 |
| [`EXU_GROUPTABLE`](#exu_grouptable) | X, CS | 3 |
| [`EXU_INSTALLATION`](#exu_installation) | X, CS | 6 |
| [`EXU_METERJOURNALTRANS`](#exu_meterjournaltrans) | X | 11 |
| [`EXU_METERPOINTSTATUSPERIOD`](#exu_meterpointstatusperiod) | X | 9 |
| [`EXU_METERTABLETRADE`](#exu_metertabletrade) | X, CS | 11 |
| [`EXU_PRICEELEMENTCHECK`](#exu_priceelementcheck) | X | 5 |
| [`EXU_PRICEELEMENTCHECKDATA`](#exu_priceelementcheckdata) | X | 7 |
| [`EXU_PRICEELEMENTRATES`](#exu_priceelementrates) | X | 28 |
| [`EXU_PRICEELEMENTTABLE`](#exu_priceelementtable) | X, CS | 12 |
| [`EXU_PRODUCTEXTENDTABLE`](#exu_productextendtable) | X | 6 |
| [`EXU_RATETABLE`](#exu_ratetable) | X | 8 |
| [`EXU_READINGTABLE`](#exu_readingtable) | CS | 16 |
| [`EXU_READINGTABLEEXT`](#exu_readingtableext) | X, CS | 8 |
| [`EXU_STREET`](#exu_street) | X, CS | 5 |
| [`EXU_TIMEREADINGSPERIOD`](#exu_timereadingsperiod) | X | 9 |
| [`EXU_UTILITYINSTALLATION`](#exu_utilityinstallation) | X, CS | 4 |
| [`INVENTTABLE`](#inventtable) | X, CS | 6 |
| [`VDOSALESJOURNALENTRY`](#vdosalesjournalentry) | CS | 9 |
| [`VDOSALESJOURNALPRODUCT`](#vdosalesjournalproduct) | CS | 6 |

## ADDRESS

- **X:** [`Address`](../src/DataMigration.XellentData/Entities/Address.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).
- **CS:** [`Address`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/Address.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESS` | X: `FullAddress`; CS: `Address1` | X: `string (max 250)`; CS: `string (max 250)` |
| `ADDRTABLEID` | X: `Addrtableid`; CS: `Addrtableid` | X: `int`; CS: `int` |
| `CITY` | X: `City`; CS: `City` | X: `string (max 60)`; CS: `string (max 60)` |
| `COUNTRYREGIONID` | X: `Countryregionid`; CS: `Countryregionid` | X: `string (max 10)`; CS: `string (max 10)` |
| `COUNTY` | X: `County`; CS: `County` | X: `string (max 10)`; CS: `string (max 10)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `EXU_ADDRESSID` | X: `ExuAddressid`; CS: `ExuAddressid` | X: `string? (max 100)`; CS: `string? (max 100)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STATE` | X: `State`; CS: `State` | X: `string (max 10)`; CS: `string (max 10)` |
| `STREET` | X: `Street`; CS: `Street` | X: `string (max 250)`; CS: `string (max 250)` |
| `ZIPCODE` | X: `Zipcode`; CS: `Zipcode` | X: `string (max 10)`; CS: `string (max 10)` |

## ADDRESSZIPCODE

- **X:** [`AddressZipCode`](../src/DataMigration.XellentData/Entities/AddressZipCode.cs) — EF key: `DATAAREAID`, `COUNTRYREGIONID`, `ZIPCODE`, `COUNTY` (Fluent HasKey).
- **CS:** [`AddressZipCode`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/AddressZipCode.cs) — EF key: `DATAAREAID`, `COUNTRYREGIONID`, `ZIPCODE`, `COUNTY` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CITY` | X: `City`; CS: `City` | X: `string (max 60)`; CS: `string (max 60)` |
| `COUNTRYREGIONID` | X: `Countryregionid`; CS: `Countryregionid` | X: `string (max 10)`; CS: `string (max 10)` |
| `COUNTY` | X: `County`; CS: `County` | X: `string (max 10)`; CS: `string (max 10)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `ZIPCODE` | X: `Zipcode`; CS: `Zipcode` | X: `string (max 10)`; CS: `string (max 10)` |

## CONTACTPERSON

- **X:** [`ContactPerson`](../src/DataMigration.XellentData/Entities/ContactPerson.cs) — EF key: `DATAAREAID`, `CONTACTPERSONID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CONTACTPERSONID` | X: `Contactpersonid` | X: `string (max 20)` |
| `CUSTACCOUNT` | X: `Custaccount` | X: `string (max 25)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `NAME` | X: `Name` | X: `string (max 60)` |
| `RECID` | X: `Recid` | X: `long` |

## CUSTINVOICEBACKORDERLINE

- **CS:** [`CustInvoiceBackorderLine`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceBackorderLine.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `INVOICEDATE` | CS: `Invoicedate` | CS: `DateTime; SQL datetime` |
| `INVOICEID` | CS: `Invoiceid` | CS: `string (max 20)` |
| `ITEMID` | CS: `Itemid` | CS: `string (max 40)` |
| `LINENUM` | CS: `Linenum` | CS: `decimal; SQL numeric(28,12)` |
| `NAME` | CS: `Name` | CS: `string (max 1000)` |
| `ORIGSALESID` | CS: `Origsalesid` | CS: `string (max 20)` |
| `QTYBACKORDERSALES` | CS: `Qtybackordersales` | CS: `decimal; SQL numeric(28,12)` |
| `QTYORDERED` | CS: `Qtyordered` | CS: `decimal; SQL numeric(28,12)` |
| `QTYSALES` | CS: `Qtysales` | CS: `decimal; SQL numeric(28,12)` |
| `RECID` | CS: `Recid` | CS: `long` |
| `SALESID` | CS: `Salesid` | CS: `string (max 20)` |
| `SALESUNIT` | CS: `Salesunit` | CS: `string (max 10)` |

## CUSTINVOICEJOUR

- **X:** [`CustInvoiceJour`](../src/DataMigration.XellentData/Entities/CustInvoiceJour.cs) — EF key: `DATAAREAID`, `INVOICEID` (PrimaryKey attribute).
- **CS:** [`CustInvoiceJour`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceJour.cs) — EF key: `DATAAREAID`, `INVOICEID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYADDRESS` | X: `Deliveryaddress`; CS: `Deliveryaddress` | X: `string (max 250)`; CS: `string (max 250)` |
| `DUEDATE` | X: `Duedate`; CS: `Duedate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `EXU_BILLINGCATEGORY` | X: `ExuBillingcategory`; CS: `ExuBillingcategory` | X: `int`; CS: `int` |
| `EXU_CONTRACTNUM` | X: `ExuContractnum`; CS: `ExuContractnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `EXU_CUSTFREEINVOICESTATUS` | X: `ExuCustfreeinvoicestatus`; CS: `ExuCustfreeinvoicestatus` | X: `int`; CS: `int` |
| `EXU_PAYMENTMEDIA` | X: `ExuPaymentmedia`; CS: `ExuPaymentmedia` | X: `int`; CS: `int` |
| `INVOICEACCOUNT` | X: `Invoiceaccount`; CS: `Invoiceaccount` | X: `string (max 25)`; CS: `string (max 25)` |
| `INVOICEAMOUNTMST` | X: `Invoiceamountmst`; CS: `Invoiceamountmst` | X: `decimal; SQL numeric(28,12)`; CS: `decimal; SQL numeric(28,12)` |
| `INVOICEDATE` | X: `Invoicedate`; CS: `Invoicedate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `INVOICEID` | X: `Invoiceid`; CS: `Invoiceid` | X: `string (max 20)`; CS: `string (max 20)` |
| `INVOICINGADDRESS` | X: `Invoicingaddress`; CS: `Invoicingaddress` | X: `string (max 250)`; CS: `string (max 250)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `SUMTAX` | X: `Sumtax`; CS: `Sumtax` | X: `decimal; SQL numeric(28,12)`; CS: `decimal; SQL numeric(28,12)` |

## CUSTINVOICELINE

- **CS:** [`CustInvoiceLine`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceLine.cs) — EF key: `DATAAREAID`, `PARENTRECID`, `LINENUM`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `AMOUNTCUR` | CS: `Amountcur` | CS: `decimal; SQL numeric(28,12)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `DESCRIPTION` | CS: `Description` | CS: `string (max 60)` |
| `EXU_BILLINGTYPE` | CS: `ExuBillingtype` | CS: `int` |
| `EXU_DELIVERYCATEGORY` | CS: `ExuDeliverycategory` | CS: `string (max 10)` |
| `EXU_ITEMID` | CS: `ExuItemid` | CS: `string (max 40)` |
| `EXU_SALESUNIT` | CS: `ExuSalesunit` | CS: `string (max 10)` |
| `LINENUM` | CS: `Linenum` | CS: `decimal; SQL numeric(28,12)` |
| `PARENTRECID` | CS: `Parentrecid` | CS: `long` |
| `QTY` | CS: `Qty` | CS: `decimal; SQL numeric(28,12)` |
| `RECID` | CS: `Recid` | CS: `long` |
| `SALESPRICE` | CS: `Salesprice` | CS: `decimal; SQL numeric(28,12)` |
| `TAXAMOUNT` | CS: `Taxamount` | CS: `decimal; SQL numeric(28,12)` |

## CUSTINVOICESALESLINK

- **CS:** [`CustInvoiceSalesLink`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceSalesLink.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `DELIVERYADDRESS` | CS: `Deliveryaddress` | CS: `string (max 250)` |
| `DELIVERYNAME` | CS: `Deliveryname` | CS: `string (max 60)` |
| `INVOICEACCOUNT` | CS: `Invoiceaccount` | CS: `string (max 25)` |
| `INVOICEDATE` | CS: `Invoicedate` | CS: `DateTime; SQL datetime` |
| `INVOICEID` | CS: `Invoiceid` | CS: `string (max 20)` |
| `INVOICINGADDRESS` | CS: `Invoicingaddress` | CS: `string (max 250)` |
| `INVOICINGNAME` | CS: `Invoicingname` | CS: `string (max 60)` |
| `ORDERACCOUNT` | CS: `Orderaccount` | CS: `string (max 25)` |
| `ORIGSALESID` | CS: `Origsalesid` | CS: `string (max 20)` |
| `RECID` | CS: `Recid` | CS: `long` |
| `SALESID` | CS: `Salesid` | CS: `string (max 20)` |

## CUSTINVOICETABLE

- **CS:** [`CustInvoiceTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceTable.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESS` | CS: `Address` | CS: `string (max 250)` |
| `CURRENCYCODE` | CS: `Currencycode` | CS: `string (max 3)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `DUEDATE` | CS: `Duedate` | CS: `DateTime; SQL datetime` |
| `EXU_BILLINGCATEGORY` | CS: `ExuBillingcategory` | CS: `int?` |
| `EXU_CONTRACTNUM` | CS: `ExuContractnum` | CS: `string (max 10)` |
| `EXU_CUSTFREEINVOICESTATUS` | CS: `ExuCustfreeinvoicestatus` | CS: `int` |
| `EXU_INVOICEPERIODENDDATE` | CS: `ExuInvoiceperiodenddate` | CS: `DateTime?; SQL datetime` |
| `EXU_INVOICEPERIODSTARTDATE` | CS: `ExuInvoiceperiodstartdate` | CS: `DateTime?; SQL datetime` |
| `EXU_INVOICESALESAMOUNT` | CS: `ExuInvoicesalesamount` | CS: `decimal?; SQL numeric(28,12)` |
| `EXU_PAYMENTMEDIA` | CS: `ExuPaymentmedia` | CS: `int?` |
| `INVOICEACCOUNT` | CS: `Invoiceaccount` | CS: `string (max 25)` |
| `INVOICEDATE` | CS: `Invoicedate` | CS: `DateTime; SQL datetime` |
| `INVOICEID` | CS: `Invoiceid` | CS: `string (max 20)` |
| `NAME` | CS: `Name` | CS: `string (max 60)` |
| `ORDERACCOUNT` | CS: `Orderaccount` | CS: `string (max 25)` |
| `POSTED` | CS: `Posted` | CS: `int` |
| `RECID` | CS: `Recid` | CS: `long` |

## CUSTINVOICETRANS

- **CS:** [`CustInvoiceTrans`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceTrans.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CURRENCYCODE` | CS: `Currencycode` | CS: `string (max 3)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `EXU_BILLINGTYPE` | CS: `ExuBillingtype` | CS: `int` |
| `EXU_COMPANYID` | CS: `ExuCompanyid` | CS: `string (max 4)` |
| `EXU_DELIVERYCATEGORY` | CS: `ExuDeliverycategory` | CS: `string (max 10)` |
| `EXU_ITEMID` | CS: `ExuItemid` | CS: `string (max 40)` |
| `INVOICEDATE` | CS: `Invoicedate` | CS: `DateTime; SQL datetime` |
| `INVOICEID` | CS: `Invoiceid` | CS: `string (max 20)` |
| `ITEMID` | CS: `Itemid` | CS: `string (max 40)` |
| `LINEAMOUNT` | CS: `Lineamount` | CS: `decimal; SQL numeric(28,12)` |
| `LINEAMOUNTMST` | CS: `Lineamountmst` | CS: `decimal; SQL numeric(28,12)` |
| `LINEAMOUNTTAX` | CS: `Lineamounttax` | CS: `decimal; SQL numeric(28,12)` |
| `LINEAMOUNTTAXMST` | CS: `Lineamounttaxmst` | CS: `decimal; SQL numeric(28,12)` |
| `LINENUM` | CS: `Linenum` | CS: `decimal; SQL numeric(28,12)` |
| `NAME` | CS: `Name` | CS: `string (max 1000)` |
| `QTY` | CS: `Qty` | CS: `decimal; SQL numeric(28,12)` |
| `RECID` | CS: `Recid` | CS: `long` |
| `SALESPRICE` | CS: `Salesprice` | CS: `decimal; SQL numeric(28,12)` |
| `SALESUNIT` | CS: `Salesunit` | CS: `string (max 10)` |
| `TAXAMOUNT` | CS: `Taxamount` | CS: `decimal; SQL numeric(28,12)` |
| `TAXAMOUNTMST` | CS: `Taxamountmst` | CS: `decimal; SQL numeric(28,12)` |

## CUSTTABLE

- **X:** [`CustTable`](../src/DataMigration.XellentData/Entities/CustTable.cs) — EF key: `DATAAREAID`, `ACCOUNTNUM` (Fluent HasKey).
- **CS:** [`CustTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustTable.cs) — EF key: `DATAAREAID`, `ACCOUNTNUM` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ACCOUNTNUM` | X: `Accountnum`; CS: `Accountnum` | X: `string (max 25)`; CS: `string (max 25)` |
| `ADDRESS` | X: `Address`; CS: `Address` | X: `string (max 250)`; CS: `string (max 250)` |
| `CELLULARPHONE` | X: `Cellularphone`; CS: `Cellularphone` | X: `string (max 20)`; CS: `string (max 20)` |
| `CITY` | X: `City`; CS: `City` | X: `string (max 60)`; CS: `string (max 60)` |
| `COMPANYREGNUM` | X: `Companyregnum`; CS: `Companyregnum` | X: `string (max 15)`; CS: `string (max 15)` |
| `COUNTRYREGIONID` | X: `Countryregionid`; CS: `Countryregionid` | X: `string (max 10)`; CS: `string (max 10)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `EMAIL` | X: `Email`; CS: `Email` | X: `string (max 80)`; CS: `string (max 80)` |
| `EXU_ADDITIONALNAME` | X: `ExuAdditionalname`; CS: `ExuAdditionalname` | X: `string (max 60)`; CS: `string (max 60)` |
| `EXU_ALTERNATIVECUSTOMERCODE` | X: `Exualternativecustomercode` | X: `int` |
| `EXU_ALTERNATIVECUSTOMERNUMBER` | X: `Exualternativecustomernumber` | X: `string (max 50)` |
| `EXU_CPR_DISPONENT1` | X: `ExuCprDisponent1`; CS: `ExuCprDisponent1` | X: `string (max 10)`; CS: `string (max 10)` |
| `EXU_CPR_DISPONENT2` | X: `ExuCprDisponent2`; CS: `ExuCprDisponent2` | X: `string (max 10)`; CS: `string (max 10)` |
| `EXU_CVR_DISPONENT1` | X: `ExuCvrDisponent1`; CS: `ExuCvrDisponent1` | X: `string (max 8)`; CS: `string (max 8)` |
| `EXU_CVR_DISPONENT2` | X: `ExuCvrDisponent2`; CS: `ExuCvrDisponent2` | X: `string (max 8)`; CS: `string (max 8)` |
| `EXU_TECHNICGROUP4` | X: `ExuTechnicgroup4`; CS: `ExuTechnicgroup4` | X: `string (max 20)`; CS: `string (max 20)` |
| `IDENTIFICATIONNUMBER` | X: `Identificationnumber`; CS: `Identificationnumber` | X: `string (max 50)`; CS: `string (max 50)` |
| `NAME` | X: `Name`; CS: `Name` | X: `string (max 60)`; CS: `string (max 60)` |
| `NAMEALIAS` | X: `Namealias`; CS: `Namealias` | X: `string (max 20)`; CS: `string (max 20)` |
| `PARTYID` | X: `Partyid`; CS: `Partyid` | X: `string (max 20)`; CS: `string (max 20)` |
| `PARTYTYPE` | X: `Partytype`; CS: `Partytype` | X: `int`; CS: `int` |
| `PHONE` | X: `Phone`; CS: `Phone` | X: `string (max 20)`; CS: `string (max 20)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STREET` | X: `Street`; CS: `Street` | X: `string (max 250)`; CS: `string (max 250)` |
| `TELEFAX` | X: `Telefax`; CS: `Telefax` | X: `string (max 20)`; CS: `string (max 20)` |
| `ZIPCODE` | X: `Zipcode`; CS: `Zipcode` | X: `string (max 10)`; CS: `string (max 10)` |

## CUSTTRANS

- **CS:** [`CustTrans`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustTrans.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ACCOUNTNUM` | CS: `Accountnum` | CS: `string (max 25)` |
| `AMOUNTCUR` | CS: `Amountcur` | CS: `decimal; SQL numeric(28,12)` |
| `AMOUNTMST` | CS: `Amountmst` | CS: `decimal; SQL numeric(28,12)` |
| `CLOSED` | CS: `Closed` | CS: `DateTime; SQL datetime` |
| `CURRENCYCODE` | CS: `Currencycode` | CS: `string (max 3)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `INVOICE` | CS: `Invoice` | CS: `string (max 20)` |
| `LASTSETTLEDATE` | CS: `Lastsettledate` | CS: `DateTime; SQL datetime` |
| `RECID` | CS: `Recid` | CS: `long` |
| `TRANSDATE` | CS: `Transdate` | CS: `DateTime; SQL datetime` |
| `TRANSTYPE` | CS: `Transtype` | CS: `int` |
| `TXT` | CS: `Txt` | CS: `string (max 60)` |
| `VOUCHER` | CS: `Voucher` | CS: `string (max 20)` |

## DIRECOMMUNICATIONADDRESS

- **X:** [`DirECommunicationAddress`](../src/DataMigration.XellentData/Entities/DirECommunicationAddress.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `ECOMMUNICATIONTYPEID` | X: `Ecommunicationtypeid` | X: `string (max 20)` |
| `EMAIL` | X: `Email` | X: `string (max 80)` |
| `PHONE` | X: `Phone` | X: `string (max 20)` |
| `RECID` | X: `Recid` | X: `long` |
| `URL` | X: `Url` | X: `string (max 255)` |

## DIRPARTYADDRESSRELATIONSHI1066

- **X:** [`DirPartyAddressRelationshi1066`](../src/DataMigration.XellentData/Entities/DirPartyAddressRelationshi1066.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).
- **CS:** [`DirPartyAddressRelationshi1066`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/DirPartyAddressRelationshi1066.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESSRECID` | X: `Addressrecid`; CS: `Addressrecid` | X: `long`; CS: `long` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `PARTYADDRESSRELATIONSHIPRECID` | X: `Partyaddressrelationshiprecid`; CS: `Partyaddressrelationshiprecid` | X: `long`; CS: `long` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |

## DIRPARTYADDRESSRELATIONSHIP

- **X:** [`DirPartyAddressRelationship`](../src/DataMigration.XellentData/Entities/DirPartyAddressRelationship.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).
- **CS:** [`DirPartyAddressRelationship`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/DirPartyAddressRelationship.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `ISPRIMARY` | X: `Isprimary`; CS: `Isprimary` | X: `int`; CS: `int` |
| `PARTYID` | X: `Partyid`; CS: `Partyid` | X: `string (max 20)`; CS: `string (max 20)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `VALIDTODATETIME` | X: `Validtodatetime`; CS: `Validtodatetime` | X: `DateTime`; CS: `DateTime` |

## DIRPARTYECOMMUNICATIONRELA2608

- **X:** [`DirPartyECommunicationRela2608`](../src/DataMigration.XellentData/Entities/DirPartyECommunicationRela2608.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `PARTYID` | X: `Partyid` | X: `string (max 20)` |
| `PRIORITY` | X: `Priority` | X: `int` |
| `RECID` | X: `Recid` | X: `long` |
| `STATUS` | X: `Status` | X: `int` |
| `VALUESRECID` | X: `Valuesrecid` | X: `long` |

## DOCUREF

- **X:** [`DocuRef`](../src/DataMigration.XellentData/Entities/DocuRef.cs) — EF key: `RECID` (Key attribute).
- **CS:** [`DocuRef`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/DocuRef.cs) — EF key: `RECID` (Key attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ACTUALCOMPANYID` | X: `Actualcompanyid`; CS: `Actualcompanyid` | X: `string? (max 4)`; CS: `string? (max 4)` |
| `CREATEDBY` | X: `Createdby`; CS: `Createdby` | X: `string? (max 5)`; CS: `string? (max 5)` |
| `CREATEDDATETIME` | X: `Createddatetime`; CS: `Createddatetime` | X: `DateTime`; CS: `DateTime` |
| `MODIFIEDBY` | X: `Modifiedby`; CS: `Modifiedby` | X: `string? (max 5)`; CS: `string? (max 5)` |
| `MODIFIEDDATETIME` | X: `Modifieddatetime`; CS: `Modifieddatetime` | X: `DateTime?`; CS: `DateTime?` |
| `NAME` | X: `Name`; CS: `Name` | X: `string (max 60)`; CS: `string (max 60)` |
| `NOTES` | X: `Notes`; CS: `Notes` | X: `string?`; CS: `string?` |
| `PARTYID` | X: `Partyid`; CS: `Partyid` | X: `string (max 20)`; CS: `string (max 20)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `REFCOMPANYID` | X: `Refcompanyid`; CS: `Refcompanyid` | X: `string? (max 4)`; CS: `string? (max 4)` |
| `REFRECID` | X: `Refrecid`; CS: `Refrecid` | X: `long`; CS: `long` |
| `REFTABLEID` | X: `Reftableid`; CS: `Reftableid` | X: `int`; CS: `int` |
| `RESTRICTION` | X: `Restriction`; CS: `Restriction` | X: `int`; CS: `int` |
| `TYPEID` | X: `Typeid`; CS: `Typeid` | X: `string (max 10)`; CS: `string (max 10)` |
| `VALUERECID` | X: `Valuerecid`; CS: `Valuerecid` | X: `long`; CS: `long` |

## DOCUTYPE

- **CS:** [`DocuType`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/DocuType.cs) — EF key: `DATAAREAID`, `TYPEID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ARCHIVEPATH` | CS: `Archivepath` | CS: `string (max 255)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `TYPEID` | CS: `Typeid` | CS: `string (max 10)` |

## DOCUVALUE

- **CS:** [`DocuValue`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/DocuValue.cs) — EF key: `RECID` (Key attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `FILENAME` | CS: `Filename` | CS: `string (max 255)` |
| `FILETYPE` | CS: `Filetype` | CS: `string (max 10)` |
| `PATH` | CS: `Path` | CS: `string (max 255)` |
| `RECID` | CS: `Recid` | CS: `long` |

## EGS_METERPOINTTYPECODEPERIOD

- **X:** [`EgsMeterpointTypecodeperiod`](../src/DataMigration.XellentData/Entities/EgsMeterpointTypecodeperiod.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).
- **CS:** [`EgsMeterpointTypecodeperiod`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/EgsMeterpointTypecodeperiod.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ATTACHMENTNUM` | X: `Attachmentnum`; CS: `Attachmentnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum`; CS: `Delpointnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `METERPOINTTYPECODE` | X: `Meterpointtypecode`; CS: `Meterpointtypecode` | X: `string (max 10)`; CS: `string (max 10)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |

## EMS_TIMESERIES

- **X:** [`EmsTimeseries`](../src/DataMigration.XellentData/Entities/EmsTimeseries.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).
- **CS:** [`EmsTimeseries`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/EmsTimeseries.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `COMPANYID` | X: `Companyid` | X: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `METERINGPOINT` | X: `Meteringpoint`; CS: `Meteringpoint` | X: `string (max 30)`; CS: `string (max 30)` |
| `NAME` | X: `Name`; CS: `Name` | X: `string (max 200)`; CS: `string (max 200)` |
| `QUANTITYCODEVALUE` | X: `Quantitycodevalue`; CS: `Quantitycodevalue` | X: `string (max 10)`; CS: `string (max 10)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `TIMERESOLUTION` | X: `Timeresolution`; CS: `Timeresolution` | X: `int`; CS: `int` |

## EMS_TIMESERIESVALUES

- **X:** [`EmsTimeseriesValues`](../src/DataMigration.XellentData/Entities/EmsTimeseriesValues.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).
- **CS:** [`EmsTimeseriesValues`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/EmsTimeseriesValues.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `QUALITYOFVALUE` | X: `Qualityofvalue`; CS: `Qualityofvalue` | X: `int`; CS: `int` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `TIMEOFVALUE` | X: `Timeofvalue`; CS: `Timeofvalue` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `TIMESERIESREFRECID` | X: `Timeseriesrefrecid`; CS: `Timeseriesrefrecid` | X: `long`; CS: `long` |
| `VALUE` | X: `Value`; CS: `Value` | X: `decimal; SQL numeric(28, 12)`; CS: `decimal; SQL numeric(28, 12)` |
| `VERSIONTOEXCL` | X: `Versiontoexcl`; CS: `Versiontoexcl` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |

## EXU_AGREEMENTTABLE

- **X:** [`ExuAgreementTable`](../src/DataMigration.XellentData/Entities/ExuAgreementTable.cs) — EF key: `DATAAREAID`, `AGREEMENTNUM`, `COMPANYID` (Fluent HasKey).
- **CS:** [`ExuAgreementTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuAgreementTable.cs) — EF key: `DATAAREAID`, `AGREEMENTNUM`, `COMPANYID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRLOCATION` | X: `Addrlocation` | X: `string? (max 35)` |
| `AGREEMENTNUM` | X: `Agreementnum`; CS: `Agreementnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `APARTMENT` | X: `Apartment` | X: `string? (max 10)` |
| `CITY` | X: `City` | X: `string? (max 60)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `COUNTRY` | X: `Country` | X: `string? (max 10)` |
| `COUNTYCODE` | X: `Countycode` | X: `string? (max 10)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `EMS_AGREEMENTSTATUSEBIX` | X: `EmsAgreementstatusebix` | X: `int` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `FLOOR` | X: `Floor` | X: `string? (max 10)` |
| `HOUSELETTERSTART` | X: `Houseletterstart` | X: `string? (max 1)` |
| `HOUSENUMSTART` | X: `Housenumstart` | X: `int?` |
| `INSTBILLINGNAME` | X: `Instbillingname` | X: `string? (max 60)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `STREET` | X: `Street` | X: `string? (max 250)` |
| `STREETNUM` | X: `Streetnum` | X: `string? (max 10)` |
| `ZIPCODEID` | X: `Zipcodeid` | X: `string? (max 10)` |

## EXU_BILLINGMETHODPERIOD

- **X:** [`ExuBillingMethodPeriod`](../src/DataMigration.XellentData/Entities/ExuBillingMethodPeriod.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).
- **CS:** [`ExuBillingMethodPeriod`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuBillingMethodPeriod.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ATTACHMENTNUM` | X: `Attachmentnum`; CS: `Attachmentnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `BILLINGMETHOD` | X: `Billingmethod`; CS: `Billingmethod` | X: `int`; CS: `int` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum`; CS: `Delpointnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |

## EXU_BILLINGPLAN

- **CS:** [`ExuBillingPlan`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuBillingPlan.cs) — EF key: `DATAAREAID`, `BILLINGPLANNUM` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `BILLINGPLANNUM` | CS: `Billingplannum` | CS: `string (max 20)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `NAME` | CS: `Name` | CS: `string (max 60)` |

## EXU_CONTRACTPARTLOG

- **X:** [`ExuContractPartLog`](../src/DataMigration.XellentData/Entities/ExuContractPartLog.cs) — EF key: `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `AMRCODE` | X: `Amrcode` | X: `string (max 10)` |
| `COMPANYID` | X: `Companyid` | X: `string (max 4)` |
| `CONTRACTNUM` | X: `Contractnum` | X: `string (max 10)` |
| `CONTRACTPARTENDDATE` | X: `Contractpartenddate` | X: `DateTime; SQL datetime` |
| `CONTRACTPARTSTARTDATE` | X: `Contractpartstartdate` | X: `DateTime; SQL datetime` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `DELIVERYGROUPNUM_RATECODE` | X: `Deliverygroupnumratecode` | X: `string? (max 20)` |
| `ERRORMSG` | X: `Errormsg` | X: `string? (max 250)` |
| `INSTAGREENUM` | X: `Instagreenum` | X: `string (max 10)` |
| `LOGDATETIME` | X: `Logdatetime` | X: `DateTime; SQL datetime` |
| `LOGEMPLID` | X: `Logemplid` | X: `string? (max 25)` |
| `PRODUCTNUM` | X: `Productnum` | X: `string (max 10)` |
| `RECID` | X: `Recid` | X: `long` |

## EXU_CONTRACTPARTTABLE

- **X:** [`ExuContractPartTable`](../src/DataMigration.XellentData/Entities/ExuContractPartTable.cs) — EF key: `DATAAREAID`, `CONTRACTNUM`, `INSTAGREENUM`, `DELIVERYCATEGORY`, `STARTDATE`, `COMPANYID`, `PRODUCTNUM`, `AMRCODE`, `CONTRACTPARTSUBREFERENCETYPE`, `CONTRACTPARTSUBREFERENCEID` (Fluent HasKey).
- **CS:** [`ExuContractPartTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuContractPartTable.cs) — EF key: `DATAAREAID`, `CONTRACTNUM`, `INSTAGREENUM`, `DELIVERYCATEGORY`, `STARTDATE`, `COMPANYID`, `PRODUCTNUM`, `AMRCODE`, `CONTRACTPARTSUBREFERENCETYPE`, `CONTRACTPARTSUBREFERENCEID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `AMRCODE` | X: `Amrcode`; CS: `Amrcode` | X: `string (max 10)`; CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `CONTRACTNUM` | X: `Contractnum`; CS: `Contractnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `CONTRACTPARTSUBREFERENCEID` | X: `Contractpartsubreferenceid`; CS: `Contractpartsubreferenceid` | X: `string (max 35)`; CS: `string (max 35)` |
| `CONTRACTPARTSUBREFERENCETYPE` | X: `Contractpartsubreferencetype`; CS: `Contractpartsubreferencetype` | X: `int`; CS: `int` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `DELIVERYGROUPNUM_RATECODE` | X: `Deliverygroupnumratecode` | X: `string? (max 20)` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `INSTAGREENUM` | X: `Instagreenum`; CS: `Instagreenum` | X: `string (max 10)`; CS: `string (max 10)` |
| `PRODUCTNUM` | X: `Productnum`; CS: `Productnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |

## EXU_CONTRACTTABLE

- **X:** [`ExuContractTable`](../src/DataMigration.XellentData/Entities/ExuContractTable.cs) — EF key: `DATAAREAID`, `CONTRACTNUM` (Fluent HasKey).
- **CS:** [`ExuContractTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuContractTable.cs) — EF key: `DATAAREAID`, `CONTRACTNUM` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESS_RECID` | X: `AddressRecid`; CS: `AddressRecid` | X: `long`; CS: `long` |
| `BILLINGBASIS` | X: `Billingbasis`; CS: `Billingbasis` | X: `int`; CS: `int` |
| `BILLINGPLANNUM` | X: `Billingplannum`; CS: `Billingplannum` | X: `string (max 10)`; CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `CONTRACTENDDATE` | X: `Contractenddate`; CS: `Contractenddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `CONTRACTNUM` | X: `Contractnum`; CS: `Contractnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `CONTRACTSTARTDATE` | X: `Contractstartdate`; CS: `Contractstartdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `CONTRACTTEMPLATENUM` | X: `Contracttemplatenum`; CS: `Contracttemplatenum` | X: `string (max 10)`; CS: `string (max 10)` |
| `CUSTACCOUNT` | X: `Custaccount`; CS: `Custaccount` | X: `string (max 25)`; CS: `string (max 25)` |
| `CUSTCONTACTPERSONID` | X: `Custcontactpersonid` | X: `string? (max 25)` |
| `CUSTPAYMCODE` | X: `Custpaymcode`; CS: `Custpaymcode` | X: `string (max 10)`; CS: `string (max 10)` |
| `CUSTPURCHASEORDER` | X: `Custpurchaseorder` | X: `string? (max 25)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `EINVOICEACCOUNTCODE` | X: `Einvoiceaccountcode` | X: `string? (max 25)` |
| `EINVOICEEANNUM` | X: `Einvoiceeannum`; CS: `Einvoiceeannum` | X: `string (max 14)`; CS: `string (max 14)` |
| `MOBILEPAYAGREEMENTCANCELDATE` | X: `Mobilepayagreementcanceldate`; CS: `Mobilepayagreementcanceldate` | X: `DateTime?; SQL datetime`; CS: `DateTime?; SQL datetime` |
| `MOBILEPAYSUBSCRIPTIONAGRE20135` | X: `Mobilepaysubscriptionagre20135`; CS: `Mobilepaysubscriptionagre20135` | X: `string (max 60)`; CS: `string (max 60)` |
| `NAME` | X: `Name`; CS: `Name` | X: `string (max 2000)`; CS: `string (max 2000)` |
| `PBSAGREEMENTNUM` | X: `Pbsagreementnum`; CS: `Pbsagreementnum` | X: `int`; CS: `int` |
| `PBSTRANSACTIONCODE` | X: `Pbstransactioncode` | X: `int` |
| `PBSTRANSDATE` | X: `Pbstransdate` | X: `DateTime; SQL datetime` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `REFERENCETOMAINCONTRACT` | X: `Referencetomaincontract`; CS: `Referencetomaincontract` | X: `string? (max 10)`; CS: `string? (max 10)` |

## EXU_CONTRACTTEMPLATE

- **CS:** [`ExuContractTemplate`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuContractTemplate.cs) — EF key: `DATAAREAID`, `CONTRACTTEMPLATENUM` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CONTRACTTEMPLATENUM` | CS: `Contracttemplatenum` | CS: `string (max 20)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `NAME` | CS: `Name` | CS: `string (max 60)` |

## EXU_DELPOINT

- **X:** [`ExuDelpoint`](../src/DataMigration.XellentData/Entities/ExuDelpoint.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `NUM` (Fluent HasKey).
- **CS:** [`ExuDelpoint`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuDelpoint.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `NUM` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESSID` | X: `Addressid`; CS: `Addressid` | X: `string (max 36)`; CS: `string (max 36)` |
| `ADDRESSSTREET` | X: `Addressstreet`; CS: `Addressstreet` | X: `string (max 132)`; CS: `string (max 132)` |
| `ADDRLOCATION` | X: `Addrlocation`; CS: `Addrlocation` | X: `string (max 35)`; CS: `string (max 35)` |
| `APARTMENT` | X: `Apartment`; CS: `Apartment` | X: `string (max 10)`; CS: `string (max 10)` |
| `ATTACHMENTNUM` | X: `Attachmentnum`; CS: `Attachmentnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `CONNECTIONTYPE` | X: `Connectiontype` | X: `int?` |
| `COUNTYNUM` | X: `Countynum`; CS: `Countynum` | X: `string (max 10)`; CS: `string (max 10)` |
| `CUSTACCOUNT` | X: `Custaccount`; CS: `Custaccount` | X: `string (max 25)`; CS: `string (max 25)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `DEL_PARENTDELPOINTNUM` | X: `DelParentdelpointnum`; CS: `DelParentdelpointnum` | X: `string? (max 10)`; CS: `string? (max 10)` |
| `EMS_NETAREA` | X: `EmsNetarea`; CS: `EmsNetarea` | X: `string (max 10)`; CS: `string (max 10)` |
| `EXTNUM` | X: `Extnum`; CS: `Extnum` | X: `string (max 18)`; CS: `string (max 18)` |
| `FLOOR` | X: `Floor`; CS: `Floor` | X: `string (max 10)`; CS: `string (max 10)` |
| `GSRN` | X: `Gsrn`; CS: `Gsrn` | X: `string (max 18)`; CS: `string (max 18)` |
| `HOUSELETTERSTART` | X: `Houseletterstart`; CS: `Houseletterstart` | X: `string (max 1)`; CS: `string (max 1)` |
| `HOUSENUMSTART` | X: `Housenumstart`; CS: `Housenumstart` | X: `int?`; CS: `int?` |
| `METERINGPOINT` | X: `Meteringpoint`; CS: `Meteringpoint` | X: `string (max 30)`; CS: `string (max 30)` |
| `NAME` | X: `Name`; CS: `Name` | X: `string (max 60)`; CS: `string (max 60)` |
| `NUM` | X: `Num`; CS: `Num` | X: `string (max 10)`; CS: `string (max 10)` |
| `POWEREXCHANGEAREA` | X: `Powerexchangearea`; CS: `Powerexchangearea` | X: `string (max 10)`; CS: `string (max 10)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STREETNUM` | X: `Streetnum`; CS: `Streetnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `ZIPCODEID` | X: `Zipcodeid`; CS: `Zipcodeid` | X: `string (max 10)`; CS: `string (max 10)` |

## EXU_DELPOINT2DELPOINTRELA25505

- **X:** [`ExuDelpoint2delpointrela25505`](../src/DataMigration.XellentData/Entities/ExuDelpoint2delpointrela25505.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).
- **CS:** [`ExuDelpoint2delpointrela25505`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuDelpoint2delpointrela25505.cs) — EF key: `DATAAREAID`, `RECID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ATTACHMENT` | X: `Attachment`; CS: `Attachment` | X: `int`; CS: `int` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `PRIMARYATTACHMENTNUM` | X: `Primaryattachmentnum`; CS: `Primaryattachmentnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `PRIMARYDELPOINTNUM` | X: `Primarydelpointnum`; CS: `Primarydelpointnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `RECVERSION` | X: `Recversion`; CS: `Recversion` | X: `int`; CS: `int` |
| `RELATIONTYPE` | X: `Relationtype`; CS: `Relationtype` | X: `int`; CS: `int` |
| `SECONDARYATTACHMENTNUM` | X: `Secondaryattachmentnum`; CS: `Secondaryattachmentnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `SECONDARYDELPOINTNUM` | X: `Secondarydelpointnum`; CS: `Secondarydelpointnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |

## EXU_DELPOINTCONTACTINFOTABLE

- **X:** [`ExuDelpointContactInfoTable`](../src/DataMigration.XellentData/Entities/ExuDelpointContactInfoTable.cs) — EF key: `DATAAREAID`, `COMPANYID`, `DELIVERYCATEGORY`, `METERINGPOINT`, `CONTACTTYPE`, `STARTDATE` (Fluent HasKey).
- **CS:** [`ExuDelpointContactInfoTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuDelpointContactInfoTable.cs) — EF key: `DATAAREAID`, `COMPANYID`, `DELIVERYCATEGORY`, `METERINGPOINT`, `CONTACTTYPE`, `STARTDATE` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESSID` | X: `Addressid`; CS: `Addressid` | X: `string (max 20)`; CS: `string (max 20)` |
| `APARTMENT` | X: `Apartment`; CS: `Apartment` | X: `string (max 10)`; CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `CONTACTTYPE` | X: `Contacttype`; CS: `Contacttype` | X: `int`; CS: `int` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `EMAIL` | X: `Email` | X: `string? (max 132)` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `FLOOR` | X: `Floor`; CS: `Floor` | X: `string (max 10)`; CS: `string (max 10)` |
| `HOUSENUMBER` | X: `Housenumber`; CS: `Housenumber` | X: `string (max 10)`; CS: `string (max 10)` |
| `METERINGPOINT` | X: `Meteringpoint`; CS: `Meteringpoint` | X: `string (max 30)`; CS: `string (max 30)` |
| `MOBILE` | X: `Mobile` | X: `string? (max 132)` |
| `NAME1` | X: `Name1`; CS: `Name1` | X: `string (max 60)`; CS: `string (max 60)` |
| `NAME2` | X: `Name2`; CS: `Name2` | X: `string (max 60)`; CS: `string (max 60)` |
| `PHONE` | X: `Phone` | X: `string? (max 132)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `STREETNAME` | X: `Streetname`; CS: `Streetname` | X: `string (max 132)`; CS: `string (max 132)` |
| `ZIPCODE` | X: `Zipcode`; CS: `Zipcode` | X: `string (max 10)`; CS: `string (max 10)` |

## EXU_DELPOINTPRICEELEMENTR25046

- **X:** [`ExuDelpointPriceElementR25046`](../src/DataMigration.XellentData/Entities/ExuDelpointPriceElementR25046.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `PARTYCHARGETYPEID` (Fluent HasKey).
- **CS:** [`ExuDelpointPriceElementR25046`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuDelpointPriceElementR25046.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `PARTYCHARGETYPEID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ATTACHMENTNUM` | X: `Attachmentnum`; CS: `Attachmentnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `CHARGETYPECODE` | X: `Chargetypecode`; CS: `Chargetypecode` | X: `int`; CS: `int` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum`; CS: `Delpointnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `PARTYCHARGETYPEID` | X: `Partychargetypeid`; CS: `Partychargetypeid` | X: `string (max 20)`; CS: `string (max 20)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `RELATIONVALIDFROM` | X: `Relationvalidfrom`; CS: `Relationvalidfrom` | X: `DateTime`; CS: `DateTime` |
| `RELATIONVALIDTO` | X: `Relationvalidto`; CS: `Relationvalidto` | X: `DateTime`; CS: `DateTime` |
| `VALIDINMARKETFROM` | X: `Validinmarketfrom`; CS: `Validinmarketfrom` | X: `DateTime`; CS: `DateTime` |
| `VALIDINMARKETTOEXCL` | X: `Validinmarkettoexcl`; CS: `Validinmarkettoexcl` | X: `DateTime`; CS: `DateTime` |

## EXU_FLEXBHISTORYLINE

- **X:** [`ExuFlexBhistoryLine`](../src/DataMigration.XellentData/Entities/ExuFlexBhistoryLine.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CALCULATEDPRICE` | X: `Calculatedprice` | X: `decimal; SQL numeric(28, 12)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DATETIME24HOUR` | X: `Datetime24hour` | X: `DateTime; SQL datetime` |
| `HISTKEYNUMBER` | X: `Histkeynumber` | X: `string (max 10)` |
| `POWEREXCHANGEPRICE` | X: `Powerexchangeprice` | X: `decimal; SQL numeric(28, 12)` |
| `RECID` | X: `Recid` | X: `long` |
| `TIMEVALUE` | X: `Timevalue` | X: `decimal; SQL numeric(28, 12)` |

## EXU_FLEXBHISTORYTABLE

- **X:** [`ExuFlexBhistoryTable`](../src/DataMigration.XellentData/Entities/ExuFlexBhistoryTable.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `BILLINGLOGNUM` | X: `Billinglognum` | X: `string (max 10)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `HISTKEYNUMBER` | X: `Histkeynumber` | X: `string (max 10)` |
| `METERINGPOINT` | X: `Meteringpoint` | X: `string (max 30)` |
| `RECID` | X: `Recid` | X: `long` |
| `REQENDDATE` | X: `Reqenddate` | X: `DateTime; SQL datetime` |
| `REQSTARTDATE` | X: `Reqstartdate` | X: `DateTime; SQL datetime` |

## EXU_GROUPTABLE

- **X:** [`ExuGroupTable`](../src/DataMigration.XellentData/Entities/ExuGroupTable.cs) — EF key: `DATAAREAID`, `GROUPNUM` (PrimaryKey attribute).
- **CS:** [`ExuGroupTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuGroupTable.cs) — EF key: `DATAAREAID`, `GROUPNUM` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `GROUPNAME` | X: `Groupname` | X: `string? (max 60)` |
| `GROUPNUM` | X: `Groupnum`; CS: `Groupnum` | X: `string (max 20)`; CS: `string (max 20)` |

## EXU_INSTALLATION

- **X:** [`ExuInstallation`](../src/DataMigration.XellentData/Entities/ExuInstallation.cs) — EF key: `DATAAREAID`, `INSTNUM` (Fluent HasKey).
- **CS:** [`ExuInstallation`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuInstallation.cs) — EF key: `DATAAREAID`, `INSTNUM` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ADDRESSID` | X: `Addressid`; CS: `Addressid` | X: `string? (max 36)`; CS: `string? (max 36)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 8)`; CS: `string (max 8)` |
| `INSTADDRESSFORMATTED` | X: `Instaddressformatted`; CS: `Instaddressformatted` | X: `string (max 250)`; CS: `string (max 250)` |
| `INSTNUM` | X: `Instnum`; CS: `Instnum` | X: `string (max 20)`; CS: `string (max 20)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `ZIPCODEID` | X: `Zipcodeid` | X: `string (max 10)` |

## EXU_METERJOURNALTRANS

- **X:** [`ExuMeterJournalTrans`](../src/DataMigration.XellentData/Entities/ExuMeterJournalTrans.cs) — EF key: `DATAAREAID`, `JOURNALID`, `LINENUM` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `COMPANYID` | X: `Companyid` | X: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `INSTALLATIONNUM` | X: `Installationnum` | X: `string (max 20)` |
| `JOURNALID` | X: `Journalid` | X: `string (max 20)` |
| `JOURNALTYPE` | X: `Journaltype` | X: `int` |
| `LINENUM` | X: `Linenum` | X: `decimal` |
| `METERNUM` | X: `Meternum` | X: `string (max 30)` |
| `POSTED` | X: `Posted` | X: `int` |
| `RECID` | X: `Recid` | X: `long` |
| `TRANSDATE` | X: `Transdate` | X: `DateTime; SQL datetime` |

## EXU_METERPOINTSTATUSPERIOD

- **X:** [`ExuMeterPointStatusPeriod`](../src/DataMigration.XellentData/Entities/ExuMeterPointStatusPeriod.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ATTACHMENTNUM` | X: `Attachmentnum` | X: `string (max 10)` |
| `COMPANYID` | X: `Companyid` | X: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum` | X: `string (max 10)` |
| `ENDDATE` | X: `Enddate` | X: `DateTime; SQL datetime` |
| `METERPOINTSTATUS` | X: `Meterpointstatus` | X: `int` |
| `RECID` | X: `Recid` | X: `long` |
| `STARTDATE` | X: `Startdate` | X: `DateTime; SQL datetime` |

## EXU_METERTABLETRADE

- **X:** [`ExuMeterTableTrade`](../src/DataMigration.XellentData/Entities/ExuMeterTableTrade.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `AGREEMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).
- **CS:** [`ExuMeterTableTrade`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuMeterTableTrade.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `AGREEMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `AGREEMENTNUM` | X: `Agreementnum`; CS: `Agreementnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string (max 4)`; CS: `string (max 4)` |
| `COUNTERCONSTANT` | X: `Counterconstant` | X: `decimal` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum`; CS: `Delpointnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `ENDDATE` | X: `Enddate`; CS: `Enddate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |
| `METERINGPOINTDHSUBTYPE` | X: `MeteringPointDhSubType` | X: `int` |
| `METERNUM` | X: `Meternum`; CS: `Meternum` | X: `string (max 30)`; CS: `string (max 30)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STARTDATE` | X: `Startdate`; CS: `Startdate` | X: `DateTime; SQL datetime`; CS: `DateTime; SQL datetime` |

## EXU_PRICEELEMENTCHECK

- **X:** [`ExuPriceElementCheck`](../src/DataMigration.XellentData/Entities/ExuPriceElementCheck.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CUSTACCOUNT` | X: `Custaccount` | X: `string (max 25)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `METERINGPOINTID` | X: `Meteringpointid` | X: `string (max 30)` |
| `RECID` | X: `Recid` | X: `long` |

## EXU_PRICEELEMENTCHECKDATA

- **X:** [`ExuPriceElementCheckData`](../src/DataMigration.XellentData/Entities/ExuPriceElementCheckData.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CHARGETYPECODE` | X: `Chargetypecode` | X: `int` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `ENDDATE` | X: `Enddate` | X: `DateTime; SQL datetime` |
| `PARTYCHARGETYPEID` | X: `Partychargetypeid` | X: `string (max 10)` |
| `PRICEELEMENTCHECKREFRECID` | X: `Priceelementcheckrefrecid` | X: `long` |
| `RECID` | X: `Recid` | X: `long` |
| `STARTDATE` | X: `Startdate` | X: `DateTime; SQL datetime` |

## EXU_PRICEELEMENTRATES

- **X:** [`ExuPriceElementRates`](../src/DataMigration.XellentData/Entities/ExuPriceElementRates.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `PARTYCHARGETYPEID` | X: `Partychargetypeid` | X: `string (max 10)` |
| `PRICE` | X: `Price` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE10_` | X: `Price10` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE11_` | X: `Price11` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE12_` | X: `Price12` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE13_` | X: `Price13` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE14_` | X: `Price14` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE15_` | X: `Price15` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE16_` | X: `Price16` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE17_` | X: `Price17` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE18_` | X: `Price18` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE19_` | X: `Price19` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE20_` | X: `Price20` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE21_` | X: `Price21` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE22_` | X: `Price22` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE23_` | X: `Price23` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE24_` | X: `Price24` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE2_` | X: `Price2` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE3_` | X: `Price3` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE4_` | X: `Price4` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE5_` | X: `Price5` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE6_` | X: `Price6` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE7_` | X: `Price7` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE8_` | X: `Price8` | X: `decimal; SQL numeric(28, 12)` |
| `PRICE9_` | X: `Price9` | X: `decimal; SQL numeric(28, 12)` |
| `RECID` | X: `Recid` | X: `long` |
| `STARTDATE` | X: `Startdate` | X: `DateTime; SQL datetime` |

## EXU_PRICEELEMENTTABLE

- **X:** [`ExuPriceElementTable`](../src/DataMigration.XellentData/Entities/ExuPriceElementTable.cs) — EF key: keyless / unspecified (Keyless).
- **CS:** [`ExuPriceElementTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuPriceElementTable.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `PARTYCHARGETYPEID`, `VALIDINMARKETFROM` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CHARGETYPECODE` | X: `Chargetypecode`; CS: `Chargetypecode` | X: `int`; CS: `int` |
| `COMPANYID` | CS: `Companyid` | CS: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | CS: `Deliverycategory` | CS: `string (max 10)` |
| `DESCRIPTION` | X: `Description` | X: `string (max 50)` |
| `ENDDATE` | CS: `Enddate` | CS: `DateTime` |
| `GRIDCOMPANYID` | X: `Gridcompanyid` | X: `decimal; SQL numeric(28, 0)` |
| `PARTYCHARGETYPEID` | X: `Partychargetypeid`; CS: `Partychargetypeid` | X: `string (max 10)`; CS: `string (max 20)` |
| `RECID` | X: `Recid` | X: `long` |
| `STARTDATE` | CS: `Startdate` | CS: `DateTime` |
| `VALIDINMARKETFROM` | CS: `Validinmarketfrom` | CS: `DateTime` |
| `VALIDINMARKETTOEXCL` | CS: `Validinmarkettoexcl` | CS: `DateTime` |

## EXU_PRODUCTEXTENDTABLE

- **X:** [`ExuProductExtendTable`](../src/DataMigration.XellentData/Entities/ExuProductExtendTable.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `ENDDATE` | X: `Enddate` | X: `DateTime; SQL datetime` |
| `PRODUCTNUM` | X: `Productnum` | X: `string (max 20)` |
| `PRODUCTTYPE` | X: `Producttype` | X: `string (max 20)` |
| `RECID` | X: `Recid` | X: `long` |
| `STARTDATE` | X: `Startdate` | X: `DateTime; SQL datetime` |

## EXU_RATETABLE

- **X:** [`ExuRateTable`](../src/DataMigration.XellentData/Entities/ExuRateTable.cs) — EF key: keyless / unspecified (Keyless).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `COMPANYID` | X: `Companyid` | X: `string (max 10)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `PRODUCTNUM` | X: `Productnum` | X: `string (max 20)` |
| `RATE` | X: `Rate` | X: `decimal; SQL numeric(28, 12)` |
| `RATETYPE` | X: `Ratetype` | X: `string (max 20)` |
| `RECID` | X: `Recid` | X: `long` |
| `STARTDATE` | X: `Startdate` | X: `DateTime; SQL datetime` |

## EXU_READINGTABLE

- **CS:** [`ExuReadingTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuReadingTable.cs) — EF key: `DATAAREAID`, `INSTNUM`, `DELIVERYCATEGORY`, `METERNUM`, `COUNTERNUM`, `READINGDATE`, `COMPANYID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `COMPANYID` | CS: `Companyid` | CS: `string (max 4)` |
| `CONSUMPTION` | CS: `Consumption` | CS: `decimal; SQL numeric(28, 12)` |
| `CONSUMPTIONPERDAY` | CS: `Consumptionperday` | CS: `decimal; SQL numeric(28, 12)` |
| `COUNTERNUM` | CS: `Counternum` | CS: `string (max 10)` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `DELIVERYCATEGORY` | CS: `Deliverycategory` | CS: `string (max 10)` |
| `DELPOINTNUM` | CS: `Delpointnum` | CS: `string? (max 20)` |
| `INSTNUM` | CS: `Instnum` | CS: `string (max 10)` |
| `METERNUM` | CS: `Meternum` | CS: `string (max 20)` |
| `METERREADING` | CS: `Meterreading` | CS: `decimal; SQL numeric(28, 12)` |
| `QUANTITYCODEVALUE` | CS: `Quantitycodevalue` | CS: `string? (max 10)` |
| `READINGDATE` | CS: `Readingdate` | CS: `DateTime; SQL datetime` |
| `READINGMETHODNUM` | CS: `Readingmethodnum` | CS: `string? (max 10)` |
| `READINGSTATUS` | CS: `Readingstatus` | CS: `int` |
| `REASONTOREADING` | CS: `Reasontoreading` | CS: `int` |
| `RECID` | CS: `Recid` | CS: `long` |

## EXU_READINGTABLEEXT

- **X:** [`ExuReadingTableExt`](../src/DataMigration.XellentData/Entities/ExuReadingTableExt.cs) — EF key: `RECID` (Fluent HasKey).
- **CS:** [`ExuReadingTableExt`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuReadingTableExt.cs) — EF key: `DATAAREAID`, `RECID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `AGREEMENTNUM` | X: `Agreementnum`; CS: `Agreementnum` | X: `string` (convention); CS: `string (max 10)` |
| `COMPANYID` | X: `Companyid`; CS: `Companyid` | X: `string` (convention); CS: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string` (convention); CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string` (convention); CS: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum`; CS: `Delpointnum` | X: `string` (convention); CS: `string (max 10)` |
| `EXPECTEDCONSUMPTIONPERYEAR` | X: `Expectedconsumptionperyear`; CS: `Expectedconsumptionperyear` | X: `decimal` (convention); CS: `decimal; SQL numeric(28, 12)` |
| `READINGDATE` | X: `Readingdate`; CS: `Readingdate` | X: `DateTime` (convention); CS: `DateTime; SQL datetime` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long` (convention); CS: `long` |

## EXU_STREET

- **X:** [`ExuStreet`](../src/DataMigration.XellentData/Entities/ExuStreet.cs) — EF key: `DATAAREAID`, `STREETNUM`, `COUNTYNUM` (Fluent HasKey).
- **CS:** [`ExuStreet`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuStreet.cs) — EF key: `DATAAREAID`, `STREETNUM`, `COUNTYNUM` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `COUNTYNUM` | X: `Countynum`; CS: `Countynum` | X: `string (max 10)`; CS: `string (max 10)` |
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `RECID` | X: `Recid`; CS: `Recid` | X: `long`; CS: `long` |
| `STREETNAME` | X: `Streetname`; CS: `Streetname` | X: `string (max 40)`; CS: `string (max 40)` |
| `STREETNUM` | X: `Streetnum`; CS: `Streetnum` | X: `string (max 10)`; CS: `string (max 10)` |

## EXU_TIMEREADINGSPERIOD

- **X:** [`ExuTimereadingsPeriod`](../src/DataMigration.XellentData/Entities/ExuTimereadingsPeriod.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, `DELPOINTNUM`, `STARTDATE` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ATTACHMENTNUM` | X: `Attachmentnum` | X: `string (max 10)` |
| `COMPANYID` | X: `Companyid` | X: `string (max 4)` |
| `DATAAREAID` | X: `Dataareaid` | X: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory` | X: `string (max 10)` |
| `DELPOINTNUM` | X: `Delpointnum` | X: `string (max 10)` |
| `ENDDATE` | X: `Enddate` | X: `DateTime; SQL datetime` |
| `RECID` | X: `Recid` | X: `long` |
| `SERIESRESOLUTION` | X: `Seriesresolution` | X: `int` |
| `STARTDATE` | X: `Startdate` | X: `DateTime; SQL datetime` |

## EXU_UTILITYINSTALLATION

- **X:** [`ExuUtilityInstallation`](../src/DataMigration.XellentData/Entities/ExuUtilityInstallation.cs) — EF key: `INSTNUM`, `DATAAREAID`, `DELIVERYCATEGORY` (Fluent HasKey).
- **CS:** [`ExuUtilityInstallation`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuUtilityInstallation.cs) — EF key: `DATAAREAID`, `DELIVERYCATEGORY`, `INSTNUM` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `DELIVERYCATEGORY` | X: `Deliverycategory`; CS: `Deliverycategory` | X: `string (max 10)`; CS: `string (max 10)` |
| `INSTNUM` | X: `Instnum`; CS: `Instnum` | X: `string (max 10)`; CS: `string (max 10)` |
| `TECHNICGROUP1` | X: `Technicgroup1`; CS: `Technicgroup1` | X: `string? (max 20)`; CS: `string? (max 20)` |

## INVENTTABLE

- **X:** [`InventTable`](../src/DataMigration.XellentData/Entities/InventTable.cs) — EF key: keyless / unspecified (Keyless).
- **CS:** [`InventTable`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/InventTable.cs) — EF key: `DATAAREAID`, `ITEMID` (PrimaryKey attribute).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `DATAAREAID` | X: `Dataareaid`; CS: `Dataareaid` | X: `string (max 4)`; CS: `string (max 4)` |
| `EXU_USERATEFROMFLEXPRICING` | X: `ExuUserateFromFlexPricing` | X: `int` |
| `ITEMID` | X: `Itemid`; CS: `Itemid` | X: `string (max 20)`; CS: `string (max 20)` |
| `ITEMNAME` | CS: `Itemname` | CS: `string (max 60)` |
| `ITEMTYPE` | X: `Itemtype` | X: `int` |
| `RECID` | X: `Recid` | X: `long` |

## VDOSALESJOURNALENTRY

- **CS:** [`Vdosalesjournalentry`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/Vdosalesjournalentry.cs) — EF key: `DATAAREAID`, `SEQUENCENO` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `CREATEDBY` | CS: `Createdby` | CS: `string (max 5)` |
| `CREATEDDATETIME` | CS: `Createddatetime` | CS: `DateTime; SQL datetime` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `FUNCTION_` | CS: `Function` | CS: `int` |
| `NOTE` | CS: `Note` | CS: `string (max 1000)` |
| `RECID` | CS: `Recid` | CS: `long` |
| `SEQUENCENO` | CS: `Sequenceno` | CS: `string (max 20)` |
| `STATUS` | CS: `Status` | CS: `int` |
| `TECHNICTEXT1` | CS: `TechnicText1` | CS: `string? (max 100)` |

## VDOSALESJOURNALPRODUCT

- **CS:** [`Vdosalesjournalproduct`](../../NewSettlement.CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/Vdosalesjournalproduct.cs) — EF key: `DATAAREAID`, `PRODUCTID` (Fluent HasKey).

| SQL column | C# property / model | Type and mapping |
|---|---|---|
| `ACTIVE` | CS: `Active` | CS: `int` |
| `DATAAREAID` | CS: `Dataareaid` | CS: `string (max 4)` |
| `DESCRIPTION` | CS: `Description` | CS: `string (max 100)` |
| `PRODUCTID` | CS: `Productid` | CS: `string (max 20)` |
| `PRODUCTTYPE` | CS: `Producttype` | CS: `int` |
| `RECID` | CS: `Recid` | CS: `long` |
