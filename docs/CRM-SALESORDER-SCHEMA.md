# Sales Order Schema Discovery Report

Generated (UTC): 2026-10-28 (live WhoAmI + EntityDefinitions + Attributes against ECRMBSS)

## Authentication result

- Endpoint: `GET /WhoAmI()`
- Status: **200 OK**
- Auth mode: Windows default credentials (`UseDefaultCredentials`)
- UserId: `b1335b28-556d-f111-afc3-005056b70b06`
- BusinessUnitId: `91a7cc62-3258-e711-8196-000d3a21a525`
- OrganizationId: `2521fc49-3b68-ea11-8265-005056a2aa00`

## Entity

- LogicalName: `salesorder`
- EntitySetName: `salesorders`
- PrimaryIdAttribute: `salesorderid`
- PrimaryNameAttribute: `name`

## Recommended `$select` fields (allowlist for POC)

- `salesorderid`
- `name`
- `ordernumber`
- `createdon`
- `modifiedon`
- `totalamount`
- `statuscode`
- `statecode`
- `_customerid_value`
- `_te_customeraccountid_value`

See also `docs/PHASE4-DYNAMIC-SALESORDER-QUERIES.md` for filter/sort operators and OptionSet labels.

## Confirmed custom field of interest

- `te_customeraccountid` — AttributeType **Lookup** (Web API value: `_te_customeraccountid_value`)

## Metadata counts

- Readable attributes returned: **218**
- Custom readable attributes: **82**
- Lookup/Customer/Owner attributes: **35**

### Sample custom attributes (first discoveries)

- `te_customeraccountid` (Lookup)
- `te_technicalcontactid` (Lookup)
- `te_billingcontactid` (Lookup)
- `te_ordertypecode` (Picklist)
- `te_emsordersn` (String)
- `te_salesforceid` (String)
- `te_sfdcid` (String)
- `te_relatedsalesorderid` (Lookup)
- `te_projectid` (Lookup)
- `te_branchaccountid` / `te_customerbranchaccountid` (Lookup)

### Customer-related lookups

- `customerid` (Customer)
- `te_customeraccountid` (Lookup)
- `te_customerbranchaccountid` (Lookup)
- `te_branchaccountid` (Lookup)

## Verified read query (backend-only)

```
GET salesorders?$select=salesorderid,name,ordernumber,createdon,statuscode,statecode,_customerid_value,_te_customeraccountid_value&$orderby=createdon desc&$top=5
```

Result: **HTTP 200**, 5 rows returned (sample names/order numbers only; not sent to Groq).

## Notes

- Field names were read from CRM metadata; they were not guessed.
- SSL certificate validation remained enabled.
- Passwords/tokens were not logged.
- Live rows are **not** exposed to AI tools until `Crm:AllowSendLiveDataToAi=true`.
