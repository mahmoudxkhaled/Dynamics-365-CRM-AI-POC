# CRM Report Entity Recommendations

Generated (UTC): 2026-11-05 09:17:04Z

## Scope

- **Discovery only** — `ApprovedEntities` was **not** modified.
- Organization context: ECRMBSS / Dynamics 365 On-Premises 9.1 (live Web API read-only).
- Currently approved: `account`, `contact`, `lead`, `salesorder`.
- EntityDefinitions inventoried: **675** (IsIntersect=false).
- Reports retrieved: **116**; system views: **1399**; user views: **0**.
- Report usage frequency: **not available** via these endpoints (not invented).
- No CRM business records or report datasets were exported.

## Evidence vs recommendation

| Kind | Meaning |
|------|---------|
| **Observed** | Present in live EntityDefinitions / reports / views metadata |
| **Recommended** | Analyst ranking for Sales/Marketing/Management reporting expansion |

### Report/view retrieval notes (observed)

- reports: retrieved 116 row(s).
- reportentities: retrieved 124 row(s).
- reportcategories: retrieved 75 row(s).
- savedqueries (querytype=0 public views): retrieved 1399 row(s).
- userqueries: retrieved 0 row(s).
- Report usage/frequency statistics are not available via these metadata endpoints; no popularity invented.
- Report binaries / fetchxml datasets / filter values were not retrieved.
- Observed: reportentities.objecttypecode is an entity logical name string on this org (not a numeric type code).
- Observed: reportentities rows often omit reportid in Web API payloads; hit counts use link-row counts per entity.
- View ReturnedTypeCode is treated as entity logical name when it matches an EntityDefinitions LogicalName.

## Current query engine constraints (observed in code)

- Supported: list, count, aggregate, groupBy, date ranges, sort, filters (max 3), single-hop and two-hop `linksJson` (max depth 2 / max 3 links).
- **Unsupported today: `operation=aggregate` + `linksJson`** (parser/builder reject). Mark such questions as unsupported until implemented.
- Aggregate root-only; detail lines (e.g. salesorderdetail) can aggregate on themselves, not as aggregate-with-parent-link in one call.

## Top recommended entities

### 1. `systemuser` — User

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=187)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 22 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 29 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** BusinessOwned; Advanced Find valid: True
- **Counts (observed):** attributes=173, relationships=2536

**Business use cases (recommendations):**
- List users — feasibility: Supported: list on systemuser
- Count orders by owner — feasibility: Supported via aggregate groupBy ownerid on salesorder; systemuser helps list enrichment

**Relevant fields (observed sample of reporting-useful attributes):**
- `exchangerate` (Decimal) — Exchange Rate
- `identityid` (Integer) — Unique user identity id
- `importsequencenumber` (Integer) — Import Sequence Number
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `userlicensetype` (Integer) — User License Type
- `versionnumber` (BigInt) — Version number
- `createdon` (DateTime) — Created On
- `modifiedon` (DateTime) — Modified On
- `overriddencreatedon` (DateTime) — Record Created On
- `ted_disableddate` (DateTime) — Disabled Date
- `accessmode` (Picklist) — Access Mode options=[0:Read-Write; 1:Administrative; 2:Read; 3:Support User; 4:Non-interactive; 5:Delegated Admin]
- `businessunitid` (Lookup) — Business Unit targets=[businessunit]
- `calendarid` (Lookup) — Calendar targets=[calendar]
- `caltype` (Picklist) — License Type options=[0:Professional; 1:Administrative; 2:Basic; 3:Device Professional; 4:Device Basic; 5:Essential]
- `createdby` (Lookup) — Created By targets=[systemuser]
- `createdonbehalfby` (Lookup) — Created By (Delegate) targets=[systemuser]
- `defaultmailbox` (Lookup) — Mailbox targets=[mailbox]
- `emailrouteraccessapproval` (Picklist) — Primary Email Status options=[0:Empty; 1:Approved; 2:Pending Approval; 3:Rejected]

- Money fields: (none in detail sample)
- DateTime fields: `modifiedon`, `overriddencreatedon`, `ted_disableddate`, `createdon`
- Status/state/picklist/owner fields: `address1_addresstypecode`, `address1_shippingmethodcode`, `invitestatuscode`, `preferredaddresscode`, `preferredemailcode`, `outgoingemaildeliverymethod`, `incomingemaildeliverymethod`, `preferredphonecode`, `emailrouteraccessapproval`, `accessmode`, `caltype`, `address2_addresstypecode`

**Relationships touching approved entities (observed, capped):**
- `actioncardusersettings_owning_user` [OneToMany] actioncardusersettings.owninguser → systemuser.systemuserid *(1:N may duplicate root rows)*
- `annotation_owning_user` [OneToMany] annotation.owninguser → systemuser.systemuserid *(1:N may duplicate root rows)*
- `bh_systemuser_bh_bosshelperauth_User` [OneToMany] bh_bosshelperauth.bh_user → systemuser.systemuserid *(1:N may duplicate root rows)*
- `bh_systemuser_bh_tabsecurity_User` [OneToMany] bh_tabsecurity.bh_user → systemuser.systemuserid *(1:N may duplicate root rows)*
- `bh_systemuser_bh_usertabs_User` [OneToMany] bh_usertabs.bh_user → systemuser.systemuserid *(1:N may duplicate root rows)*
- `bizmap_systemuser_businessid` [OneToMany] businessunitmap.businessid → systemuser.businessunitid *(1:N may duplicate root rows)*
- `business_unit_system_users` [ManyToOne] systemuser.businessunitid → businessunit.businessunitid
- `calendar_system_users` [ManyToOne] systemuser.calendarid → calendar.calendarid
- `constraintbasedgroup_systemuser` [OneToMany] constraintbasedgroup.businessunitid → systemuser.businessunitid *(1:N may duplicate root rows)*
- `contact_owning_user` [OneToMany] contact.owninguser → systemuser.systemuserid *(1:N may duplicate root rows)*
- `createdby_attributemap` [OneToMany] attributemap.createdby → systemuser.systemuserid *(1:N may duplicate root rows)*
- `createdby_connection` [OneToMany] connection.createdby → systemuser.systemuserid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 2. `account` — Account

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=135)
- **Recommended approval phase:** Phase 1
- **Currently approved:** Yes
- **Observed report evidence:** 17 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 21 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=369, relationships=174

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `aging30` (Money) — Aging 30
- `aging30_base` (Money) — Aging 30 (Base)
- `aging60` (Money) — Aging 60
- `aging60_base` (Money) — Aging 60 (Base)
- `aging90` (Money) — Aging 90
- `aging90_base` (Money) — Aging 90 (Base)
- `creditlimit` (Money) — Credit Limit
- `creditlimit_base` (Money) — Credit Limit (Base)
- `marketcap` (Money) — Market Capitalization
- `marketcap_base` (Money) — Market Capitalization (Base)
- `openrevenue` (Money) — Open Revenue
- `openrevenue_base` (Money) — Open Revenue (Base)
- `revenue` (Money) — Annual Revenue
- `revenue_base` (Money) — Annual Revenue (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `numberofemployees` (Integer) — Number of Employees
- `onholdtime` (Integer) — On Hold Time (Minutes)

- Money fields: `openrevenue_base`, `aging30`, `creditlimit_base`, `aging60`, `aging90`, `marketcap_base`, `aging90_base`, `aging60_base`, `creditlimit`, `revenue`, `revenue_base`, `aging30_base`, `openrevenue`, `marketcap`
- DateTime fields: `openrevenue_date`, `overriddencreatedon`, `lastonholdtime`, `te_suspendedinstalledbaselinerollup_date`, `te_terminatedbillingdetailsrollup_date`, `te_activebillingdetailsrollup_date`, `te_inflightcommercialorderlinerollup_date`, `createdon`, `te_activeinstalledbasedlinerollup_date`, `lastusedincampaign`, `opendeals_date`, `te_canceledcommercialorderlinerollup_date`
- Status/state/picklist/owner fields: `te_billdeliverymethodcode`, `customertypecode`, `address1_addresstypecode`, `accountclassificationcode`, `te_premiumstatuscode`, `shippingmethodcode`, `preferredappointmenttimecode`, `accountcategorycode`, `te_profiletypecode`, `address2_freighttermscode`, `paymenttermscode`, `businesstypecode`

**Relationships touching approved entities (observed, capped):**
- `account_actioncard` [OneToMany] actioncard.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `account_activity_parties` [OneToMany] activityparty.partyid → account.accountid *(1:N may duplicate root rows)*
- `Account_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `Account_Annotation` [OneToMany] annotation.objectid → account.accountid *(1:N may duplicate root rows)*
- `Account_Appointments` [OneToMany] appointment.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `Account_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `account_bookableresource_AccountId` [OneToMany] bookableresource.accountid → account.accountid *(1:N may duplicate root rows)*
- `Account_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `account_BulkOperations` [OneToMany] bulkoperation.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `account_CampaignResponses` [OneToMany] campaignresponse.regardingobjectid → account.accountid *(1:N may duplicate root rows)*
- `account_connections1` [OneToMany] connection.record1id → account.accountid *(1:N may duplicate root rows)*
- `account_connections2` [OneToMany] connection.record2id → account.accountid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 3. `opportunity` — Opportunity

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=117)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 6 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 25 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=226, relationships=80

**Business use cases (recommendations):**
- How many open opportunities by month this year? — feasibility: Supported: aggregate + date + groupBy month (root-only)
- Top opportunities with account name — feasibility: Supported: list + linksJson to account
- Opportunity revenue by account this quarter — feasibility: Unsupported until aggregate+linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualvalue` (Money) — Actual Revenue
- `actualvalue_base` (Money) — Actual Revenue (Base)
- `budgetamount` (Money) — Budget Amount
- `budgetamount_base` (Money) — Budget Amount (Base)
- `discountamount` (Money) — Opportunity Discount Amount
- `discountamount_base` (Money) — Opportunity Discount Amount (Base)
- `estimatedvalue` (Money) — Est. Revenue
- `estimatedvalue_base` (Money) — Est. Revenue (Base)
- `freightamount` (Money) — Freight Amount
- `freightamount_base` (Money) — Freight Amount (Base)
- `totalamount` (Money) — Total Amount
- `totalamount_base` (Money) — Total Amount (Base)
- `totalamountlessfreight` (Money) — Total Pre-Freight Amount
- `totalamountlessfreight_base` (Money) — Total Pre-Freight Amount (Base)
- `totaldiscountamount` (Money) — Total Discount Amount
- `totaldiscountamount_base` (Money) — Total Discount Amount (Base)
- `totallineitemamount` (Money) — Total Detail Amount
- `totallineitemamount_base` (Money) — Total Detail Amount (Base)

- Money fields: `budgetamount_base`, `freightamount`, `totallineitemamount_base`, `totallineitemdiscountamount_base`, `totaldiscountamount`, `freightamount_base`, `estimatedvalue`, `actualvalue_base`, `totalamountlessfreight_base`, `totalamountlessfreight`, `totallineitemamount`, `actualvalue`, `budgetamount`, `estimatedvalue_base`, `totaldiscountamount_base`, `totallineitemdiscountamount`, `totalamount`, `totalamount_base`, `discountamount_base`, `totaltax`, `discountamount`, `totaltax_base`
- DateTime fields: `finaldecisiondate`, `overriddencreatedon`, `ted_todaydate`, `modifiedon`, `estimatedclosedate`, `createdon`, `schedulefollowup_prospect`, `actualclosedate`, `schedulefollowup_qualify`, `scheduleproposalmeeting`, `lastonholdtime`
- Status/state/picklist/owner fields: `new_service`, `ted_closereason`, `initialcommunication`, `statuscode`, `new_type`, `opportunityratingcode`, `salesstage`, `skippricecalculation`, `new_stagestatus`, `ownerid`, `timeline`, `budgetstatus`

**Relationships touching approved entities (observed, capped):**
- `business_unit_opportunities` [ManyToOne] opportunity.owningbusinessunit → businessunit.businessunitid
- `campaign_opportunities` [ManyToOne] opportunity.campaignid → campaign.campaignid
- `CreatedOpportunity_BulkOperationLogs` [OneToMany] bulkoperationlog.createdobjectid → opportunity.opportunityid *(1:N may duplicate root rows)*
- `lead_qualifying_opportunity` [OneToMany] lead.qualifyingopportunityid → opportunity.opportunityid *(1:N may duplicate root rows)*
- `lk_leadtoopportunitysalesprocess_opportunityid` [OneToMany] leadtoopportunitysalesprocess.opportunityid → opportunity.opportunityid *(1:N may duplicate root rows)*
- `lk_opportunity_createdonbehalfby` [ManyToOne] opportunity.createdonbehalfby → systemuser.systemuserid
- `lk_opportunity_modifiedonbehalfby` [ManyToOne] opportunity.modifiedonbehalfby → systemuser.systemuserid
- `lk_opportunitybase_createdby` [ManyToOne] opportunity.createdby → systemuser.systemuserid
- `lk_opportunitybase_modifiedby` [ManyToOne] opportunity.modifiedby → systemuser.systemuserid
- `lk_opportunitysalesprocess_opportunityid` [OneToMany] opportunitysalesprocess.opportunityid → opportunity.opportunityid *(1:N may duplicate root rows)*
- `manualsla_opportunity` [ManyToOne] opportunity.slaid → sla.slaid
- `msdyn_playbookinstance_opportunity` [OneToMany] msdyn_playbookinstance.regarding → opportunity.opportunityid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 4. `campaign` — Campaign

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=111)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 11 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 8 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=70, relationships=50

**Business use cases (recommendations):**
- Campaigns started this year — feasibility: Supported: list/count + date filter
- Campaign responses by campaign — feasibility: May require campaignresponse approval; aggregate+link unsupported

**Relevant fields (observed sample of reporting-useful attributes):**
- `budgetedcost` (Money) — Budget Allocated
- `budgetedcost_base` (Money) — Budget Allocated (Base)
- `expectedrevenue` (Money) — Estimated Revenue
- `expectedrevenue_base` (Money) — Expected Revenue (Base)
- `othercost` (Money) — Miscellaneous Costs
- `othercost_base` (Money) — Miscellaneous Costs (Base)
- `totalactualcost` (Money) — Total Cost of Campaign
- `totalactualcost_base` (Money) — Total Cost of Campaign (Base)
- `totalcampaignactivityactualcost` (Money) — Total Cost of Campaign Activities
- `totalcampaignactivityactualcost_base` (Money) — Total Cost of Campaign Activities (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `expectedresponse` (Integer) — Expected Response Percentage
- `importsequencenumber` (Integer) — Import Sequence Number
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `actualend` (DateTime) — Actual End Date
- `actualstart` (DateTime) — Actual Start Date
- `createdon` (DateTime) — Created On

- Money fields: `totalactualcost_base`, `expectedrevenue_base`, `budgetedcost_base`, `expectedrevenue`, `othercost`, `totalcampaignactivityactualcost_base`, `totalcampaignactivityactualcost`, `totalactualcost`, `budgetedcost`, `othercost_base`
- DateTime fields: `createdon`, `overriddencreatedon`, `proposedstart`, `actualstart`, `modifiedon`, `proposedend`, `actualend`
- Status/state/picklist/owner fields: `statecode`, `ownerid`, `statuscode`, `typecode`

**Relationships touching approved entities (observed, capped):**
- `BusinessUnit_Campaigns` [ManyToOne] campaign.owningbusinessunit → businessunit.businessunitid
- `campaign_activity_parties` [OneToMany] activityparty.partyid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_Annotation` [OneToMany] annotation.objectid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_Appointments` [OneToMany] appointment.regardingobjectid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_CampaignActivities` [OneToMany] campaignactivity.regardingobjectid → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_CampaignResponses` [OneToMany] campaignresponse.regardingobjectid → campaign.campaignid *(1:N may duplicate root rows)*
- `campaign_connections1` [OneToMany] connection.record1id → campaign.campaignid *(1:N may duplicate root rows)*
- `campaign_connections2` [OneToMany] connection.record2id → campaign.campaignid *(1:N may duplicate root rows)*
- `Campaign_DuplicateBaseRecord` [OneToMany] duplicaterecord.baserecordid → campaign.campaignid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 5. `incident` — Case

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=94)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 9 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 15 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=150, relationships=79

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualserviceunits` (Integer) — Actual Service Units
- `billedserviceunits` (Integer) — Billed Service Units
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `influencescore` (Double) — Influence Score
- `numberofchildincidents` (Integer) — Child Cases
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `sentimentvalue` (Double) — Sentiment Value
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `createdon` (DateTime) — Created On
- `escalatedon` (DateTime) — Escalated On
- `followupby` (DateTime) — Follow Up By
- `lastonholdtime` (DateTime) — Last On Hold Time
- `modifiedon` (DateTime) — Modified On
- `overriddencreatedon` (DateTime) — Record Created On
- `resolveby` (DateTime) — Resolve By
- `responseby` (DateTime) — First Response By

- Money fields: (none in detail sample)
- DateTime fields: `modifiedon`, `overriddencreatedon`, `resolveby`, `lastonholdtime`, `createdon`, `responseby`, `followupby`, `escalatedon`
- Status/state/picklist/owner fields: `firstresponseslastatus`, `statuscode`, `servicestage`, `contractservicelevelcode`, `casetypecode`, `caseorigincode`, `ownerid`, `severitycode`, `customersatisfactioncode`, `resolvebyslastatus`, `incidentstagecode`, `messagetypecode`

**Relationships touching approved entities (observed, capped):**
- `business_unit_incidents` [ManyToOne] incident.owningbusinessunit → businessunit.businessunitid
- `contact_as_primary_contact` [ManyToOne] incident.primarycontactid → contact.contactid
- `contact_as_responsible_contact` [ManyToOne] incident.responsiblecontactid → contact.contactid
- `contract_cases` [ManyToOne] incident.contractid → contract.contractid
- `contract_detail_cases` [ManyToOne] incident.contractdetailid → contractdetail.contractdetailid
- `entitlement_cases` [ManyToOne] incident.entitlementid → entitlement.entitlementid
- `incident_actioncard` [OneToMany] actioncard.regardingobjectid → incident.incidentid *(1:N may duplicate root rows)*
- `incident_activity_parties` [OneToMany] activityparty.partyid → incident.incidentid *(1:N may duplicate root rows)*
- `Incident_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → incident.incidentid *(1:N may duplicate root rows)*
- `Incident_Annotation` [OneToMany] annotation.objectid → incident.incidentid *(1:N may duplicate root rows)*
- `Incident_Appointments` [OneToMany] appointment.regardingobjectid → incident.incidentid *(1:N may duplicate root rows)*
- `Incident_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → incident.incidentid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 6. `salesorderdetail` — Commercial Order Line

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=94)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 1 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 23 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=438, relationships=87

**Business use cases (recommendations):**
- Monthly ordered quantity by product for 2025 — feasibility: Supported if aggregating on salesorderdetail root
- Order lines for a sales order with product name — feasibility: Supported: list + linksJson product
- Total line amount by customer account — feasibility: Unsupported as aggregate+link; needs engine enhancement

**Relevant fields (observed sample of reporting-useful attributes):**
- `baseamount` (Money) — Amount
- `baseamount_base` (Money) — Amount (Base)
- `extendedamount` (Money) — Extended Amount
- `extendedamount_base` (Money) — Extended Amount (Base)
- `manualdiscountamount` (Money) — Manual Discount
- `manualdiscountamount_base` (Money) — Manual Discount (Base)
- `priceperunit` (Money) — Price Per Unit
- `priceperunit_base` (Money) — Price Per Unit (Base)
- `tax` (Money) — Tax
- `tax_base` (Money) — Tax (Base)
- `te_nrcamount` (Money) — NRC
- `te_nrcamount_base` (Money) — NRC (Base)
- `te_rcamount` (Money) — RC
- `te_rcamount_base` (Money) — RC (Base)
- `te_volumediscountamount` (Money) — Volume Discount Amount
- `te_volumediscountamount_base` (Money) — Volume Discount Amount (Base)
- `ted_tnrcamount` (Money) — TTL NRC
- `ted_tnrcamount_base` (Money) — TNRC (Base)

- Money fields: `ted_trcamount`, `extendedamount_base`, `priceperunit_base`, `extendedamount`, `priceperunit`, `te_volumediscountamount_base`, `ted_tnrcamount_base`, `ted_tnrcamount`, `te_nrcamount`, `baseamount_base`, `te_rcamount`, `te_nrcamount_base`, `te_rcamount_base`, `manualdiscountamount`, `te_volumediscountamount`, `volumediscountamount`, `volumediscountamount_base`, `tax_base`, `baseamount`, `manualdiscountamount_base`, `tax`, `ted_trcamount_base`
- DateTime fields: `te_activationdate`, `te_billingstartdate`, `te_installationrequestdate`, `ted_fulfilmentdate`, `te_installedbasecreationdate`, `te_addonexpirationdate`, `createdon`, `te_installationdate`, `te_forcechargerequesteddate`, `te_epmstatusdate`, `te_chargeseffectivedate`, `te_poexpirationdate`
- Status/state/picklist/owner fields: `ted_digitaltransformation`, `te_fulfillmentpathcode`, `ted_racktype`, `te_productfamilycode`, `te_connectedtotypecode`, `producttypecode`, `te_serviceshifttype`, `te_testperiodcode`, `te_ordertypecode`, `te_salesreactivationreasoncode`, `te_hardwareterminationreasoncode`, `ownerid`

**Relationships touching approved entities (observed, capped):**
- `lk_salesorderdetail_createdonbehalfby` [ManyToOne] salesorderdetail.createdonbehalfby → systemuser.systemuserid
- `lk_salesorderdetail_modifiedonbehalfby` [ManyToOne] salesorderdetail.modifiedonbehalfby → systemuser.systemuserid
- `lk_salesorderdetailbase_createdby` [ManyToOne] salesorderdetail.createdby → systemuser.systemuserid
- `lk_salesorderdetailbase_modifiedby` [ManyToOne] salesorderdetail.modifiedby → systemuser.systemuserid
- `msdyn_quotedetail_salesorderdetail` [ManyToOne] salesorderdetail.quotedetailid → quotedetail.quotedetailid
- `msdyn_salesorderdetail_invoicedetail` [OneToMany] invoicedetail.salesorderdetailid → salesorderdetail.salesorderdetailid *(1:N may duplicate root rows)*
- `new_contractdetail_salesorderdetail` [ManyToOne] salesorderdetail.new_te_mand_ibl_colid → contractdetail.contractdetailid
- `order_details` [ManyToOne] salesorderdetail.salesorderid → salesorder.salesorderid
- `product_order_details` [ManyToOne] salesorderdetail.productid → product.productid
- `productAssociation_salesorder_details` [ManyToOne] salesorderdetail.productassociationid → productassociation.productassociationid
- `SalesOrderDetail_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → salesorderdetail.salesorderdetailid *(1:N may duplicate root rows)*
- `SalesOrderDetail_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → salesorderdetail.salesorderdetailid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).
- Line-item aggregates joined to parent headers require either root=line-item entity or future aggregate+link support.

### 7. `product` — Product

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=83)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 2 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 13 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** OrganizationOwned; Advanced Find valid: True
- **Counts (observed):** attributes=169, relationships=77

**Business use cases (recommendations):**
- List active products — feasibility: Supported: list + status filter
- Count products by product type — feasibility: Supported: aggregate + groupBy picklist if present

**Relevant fields (observed sample of reporting-useful attributes):**
- `currentcost` (Money) — Current Cost
- `currentcost_base` (Money) — Current Cost (Base)
- `price` (Money) — List Price
- `price_base` (Money) — List Price (Base)
- `standardcost` (Money) — Standard Cost
- `standardcost_base` (Money) — Standard Cost (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `quantitydecimal` (Integer) — Decimals Supported
- `quantityonhand` (Decimal) — Quantity On Hand
- `stockvolume` (Decimal) — Stock Volume
- `stockweight` (Decimal) — Stock Weight
- `te_billingsuspensionfees` (Double) — billing Suspension Fees (%)
- `te_expecteddeliverduration` (Integer) — Expected Delivery Duration
- `te_maximumquantity` (Integer) — Maximum Quantity
- `te_minimumquantity` (Integer) — Minimum Quantity
- `te_renewalperiod` (Integer) — Renewal Period
- `te_suspensionfees` (Double) — Suspension Fees (%)

- Money fields: `currentcost_base`, `currentcost`, `standardcost_base`, `standardcost`, `price_base`, `price`
- DateTime fields: `modifiedon`, `overriddencreatedon`, `createdon`, `validfromdate`, `validtodate`
- Status/state/picklist/owner fields: `te_resourcecomponentcode`, `productstructure`, `te_resourcefamilycode`, `statuscode`, `te_transmissionmediacode`, `te_waitingperiod`, `te_productfamilycode`, `te_billingintegrationtypecode`, `te_ordertypescode`, `producttypecode`, `te_fulfillmentpathcode`, `te_flagchildsstatus`

**Relationships touching approved entities (observed, capped):**
- `lk_externalparty_product_createdby` [ManyToOne] product.createdbyexternalparty → externalparty.externalpartyid
- `lk_externalparty_product_modifiedby` [ManyToOne] product.modifiedbyexternalparty → externalparty.externalpartyid
- `lk_product_createdonbehalfby` [ManyToOne] product.createdonbehalfby → systemuser.systemuserid
- `lk_product_entityimage` [ManyToOne] product.entityimageid → imagedescriptor.imagedescriptorid
- `lk_product_modifiedonbehalfby` [ManyToOne] product.modifiedonbehalfby → systemuser.systemuserid
- `lk_productbase_createdby` [ManyToOne] product.createdby → systemuser.systemuserid
- `lk_productbase_modifiedby` [ManyToOne] product.modifiedby → systemuser.systemuserid
- `new_product_salesorder_TestProduct` [OneToMany] salesorder.new_testproduct → product.productid *(1:N may duplicate root rows)*
- `opportunity_products` [OneToMany] opportunityproduct.productid → product.productid *(1:N may duplicate root rows)*
- `organization_products` [ManyToOne] product.organizationid → organization.organizationid
- `price_level_products` [ManyToOne] product.pricelevelid → pricelevel.pricelevelid
- `processstage_products` [ManyToOne] product.stageid → processstage.processstageid

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).
- Line-item aggregates joined to parent headers require either root=line-item entity or future aggregate+link support.

### 8. `invoice` — Invoice

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=80)
- **Recommended approval phase:** Phase 1
- **Currently approved:** No
- **Observed report evidence:** 4 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 6 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=125, relationships=48

**Business use cases (recommendations):**
- Invoiced amount by month for 2025 — feasibility: Supported: aggregate on invoice root if Money + date fields exist
- Invoices with related sales order — feasibility: Supported: list + linksJson (not aggregate+link)

**Relevant fields (observed sample of reporting-useful attributes):**
- `discountamount` (Money) — Invoice Discount Amount
- `discountamount_base` (Money) — Invoice Discount Amount (Base)
- `freightamount` (Money) — Freight Amount
- `freightamount_base` (Money) — Freight Amount (Base)
- `totalamount` (Money) — Total Amount
- `totalamount_base` (Money) — Total Amount (Base)
- `totalamountlessfreight` (Money) — Total Pre-Freight Amount
- `totalamountlessfreight_base` (Money) — Total Pre-Freight Amount (Base)
- `totaldiscountamount` (Money) — Total Discount Amount
- `totaldiscountamount_base` (Money) — Total Discount Amount (Base)
- `totallineitemamount` (Money) — Total Detail Amount
- `totallineitemamount_base` (Money) — Total Detail Amount (Base)
- `totallineitemdiscountamount` (Money) — Total Line Item Discount Amount
- `totallineitemdiscountamount_base` (Money) — Total Line Item Discount Amount (Base)
- `totaltax` (Money) — Total Tax
- `totaltax_base` (Money) — Total Tax (Base)
- `discountpercentage` (Decimal) — Invoice Discount (%)
- `exchangerate` (Decimal) — Exchange Rate

- Money fields: `freightamount`, `totallineitemamount_base`, `totalamountlessfreight_base`, `discountamount_base`, `totaldiscountamount`, `totallineitemamount`, `discountamount`, `totaltax_base`, `freightamount_base`, `totalamount`, `totalamountlessfreight`, `totaltax`, `totallineitemdiscountamount_base`, `totallineitemdiscountamount`, `totaldiscountamount_base`, `totalamount_base`
- DateTime fields: `createdon`, `overriddencreatedon`, `duedate`, `datedelivered`, `modifiedon`, `lastonholdtime`, `lastbackofficesubmit`
- Status/state/picklist/owner fields: `prioritycode`, `statecode`, `shippingmethodcode`, `skippricecalculation`, `ownerid`, `paymenttermscode`, `pricingerrorcode`, `shipto_freighttermscode`, `statuscode`

**Relationships touching approved entities (observed, capped):**
- `business_unit_invoices` [ManyToOne] invoice.owningbusinessunit → businessunit.businessunitid
- `invoice_activity_parties` [OneToMany] activityparty.partyid → invoice.invoiceid *(1:N may duplicate root rows)*
- `Invoice_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → invoice.invoiceid *(1:N may duplicate root rows)*
- `Invoice_Annotation` [OneToMany] annotation.objectid → invoice.invoiceid *(1:N may duplicate root rows)*
- `Invoice_Appointments` [OneToMany] appointment.regardingobjectid → invoice.invoiceid *(1:N may duplicate root rows)*
- `Invoice_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → invoice.invoiceid *(1:N may duplicate root rows)*
- `Invoice_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → invoice.invoiceid *(1:N may duplicate root rows)*
- `invoice_connections1` [OneToMany] connection.record1id → invoice.invoiceid *(1:N may duplicate root rows)*
- `invoice_connections2` [OneToMany] connection.record2id → invoice.invoiceid *(1:N may duplicate root rows)*
- `invoice_customer_accounts` [ManyToOne] invoice.customerid → account.accountid
- `invoice_customer_contacts` [ManyToOne] invoice.customerid → contact.contactid
- `invoice_details` [OneToMany] invoicedetail.invoiceid → invoice.invoiceid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 9. `lead` — Lead

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=77)
- **Recommended approval phase:** Phase 2
- **Currently approved:** Yes
- **Observed report evidence:** 3 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 20 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=263, relationships=84

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `budgetamount` (Money) — Budget Amount
- `budgetamount_base` (Money) — Budget Amount (Base)
- `estimatedamount` (Money) — Est. Value
- `estimatedamount_base` (Money) — Est. Value (Base)
- `revenue` (Money) — Annual Revenue
- `revenue_base` (Money) — Annual Revenue (Base)
- `estimatedvalue` (Double) — Est. Value (deprecated)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `numberofemployees` (Integer) — No. of Employees
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `teamsfollowed` (Integer) — TeamsFollowed
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `createdon` (DateTime) — Created On
- `estimatedclosedate` (DateTime) — Est. Close Date
- `lastonholdtime` (DateTime) — Last On Hold Time
- `lastusedincampaign` (DateTime) — Last Campaign Date

- Money fields: `budgetamount_base`, `estimatedamount_base`, `revenue`, `revenue_base`, `budgetamount`, `estimatedamount`
- DateTime fields: `modifiedon`, `overriddencreatedon`, `estimatedclosedate`, `createdon`, `lastusedincampaign`, `schedulefollowup_qualify`, `lastonholdtime`, `schedulefollowup_prospect`
- Status/state/picklist/owner fields: `budgetstatus`, `address1_addresstypecode`, `address1_shippingmethodcode`, `ted_contactstatus`, `initialcommunication`, `address2_shippingmethodcode`, `salesstage`, `preferredcontactmethodcode`, `leadqualitycode`, `leadsourcecode`, `salesstagecode`, `statuscode`

**Relationships touching approved entities (observed, capped):**
- `account_originating_lead` [OneToMany] account.originatingleadid → lead.leadid *(1:N may duplicate root rows)*
- `business_unit_leads` [ManyToOne] lead.owningbusinessunit → businessunit.businessunitid
- `campaign_leads` [ManyToOne] lead.campaignid → campaign.campaignid
- `CampaignResponse_Lead` [ManyToOne] lead.relatedobjectid → campaignresponse.activityid
- `contact_originating_lead` [OneToMany] contact.originatingleadid → lead.leadid *(1:N may duplicate root rows)*
- `CreatedLead_BulkOperationLogs` [OneToMany] bulkoperationlog.createdobjectid → lead.leadid *(1:N may duplicate root rows)*
- `Lead_actioncard` [OneToMany] actioncard.regardingobjectid → lead.leadid *(1:N may duplicate root rows)*
- `lead_activity_parties` [OneToMany] activityparty.partyid → lead.leadid *(1:N may duplicate root rows)*
- `Lead_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → lead.leadid *(1:N may duplicate root rows)*
- `lead_addresses` [OneToMany] leadaddress.parentid → lead.leadid *(1:N may duplicate root rows)*
- `Lead_Annotation` [OneToMany] annotation.objectid → lead.leadid *(1:N may duplicate root rows)*
- `Lead_Appointments` [OneToMany] appointment.regardingobjectid → lead.leadid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 10. `contact` — Contact

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=77)
- **Recommended approval phase:** Phase 2
- **Currently approved:** Yes
- **Observed report evidence:** 4 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 17 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=322, relationships=121

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `aging30` (Money) — Aging 30
- `aging30_base` (Money) — Aging 30 (Base)
- `aging60` (Money) — Aging 60
- `aging60_base` (Money) — Aging 60 (Base)
- `aging90` (Money) — Aging 90
- `aging90_base` (Money) — Aging 90 (Base)
- `annualincome` (Money) — Annual Income
- `annualincome_base` (Money) — Annual Income (Base)
- `creditlimit` (Money) — Credit Limit
- `creditlimit_base` (Money) — Credit Limit (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `numberofchildren` (Integer) — No. of Children
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `te_contactsn` (Integer) — ContactSn
- `te_phonecode` (Decimal) — (draft)Primary Phone Code
- `te_phonecode2` (Decimal) — (draft)Secondary Phone Code
- `teamsfollowed` (Integer) — TeamsFollowed

- Money fields: `aging30`, `creditlimit_base`, `aging60`, `aging90`, `aging90_base`, `annualincome_base`, `aging60_base`, `annualincome`, `creditlimit`, `aging30_base`
- DateTime fields: `lastonholdtime`, `birthdate`, `anniversary`, `createdon`, `lastusedincampaign`, `overriddencreatedon`, `modifiedon`
- Status/state/picklist/owner fields: `haschildrencode`, `te_mobileoperatorcode`, `customertypecode`, `te_epremiumprivilegecode`, `address1_freighttermscode`, `gendercode`, `address1_addresstypecode`, `preferredappointmenttimecode`, `address2_freighttermscode`, `msdyn_orgchangestatus`, `paymenttermscode`, `accountrolecode`

**Relationships touching approved entities (observed, capped):**
- `account_primary_contact` [OneToMany] account.primarycontactid → contact.contactid *(1:N may duplicate root rows)*
- `business_unit_contacts` [ManyToOne] contact.owningbusinessunit → businessunit.businessunitid
- `contact_actioncard` [OneToMany] actioncard.regardingobjectid → contact.contactid *(1:N may duplicate root rows)*
- `contact_activity_parties` [OneToMany] activityparty.partyid → contact.contactid *(1:N may duplicate root rows)*
- `Contact_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → contact.contactid *(1:N may duplicate root rows)*
- `Contact_Annotation` [OneToMany] annotation.objectid → contact.contactid *(1:N may duplicate root rows)*
- `Contact_Appointments` [OneToMany] appointment.regardingobjectid → contact.contactid *(1:N may duplicate root rows)*
- `contact_as_primary_contact` [OneToMany] incident.primarycontactid → contact.contactid *(1:N may duplicate root rows)*
- `contact_as_responsible_contact` [OneToMany] incident.responsiblecontactid → contact.contactid *(1:N may duplicate root rows)*
- `Contact_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → contact.contactid *(1:N may duplicate root rows)*
- `contact_bookableresource_ContactId` [OneToMany] bookableresource.contactid → contact.contactid *(1:N may duplicate root rows)*
- `Contact_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → contact.contactid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 11. `opportunityproduct` — Opportunity Product

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=75)
- **Recommended approval phase:** Phase 2
- **Currently approved:** No
- **Observed report evidence:** 0 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 13 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=96, relationships=28

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `baseamount` (Money) — Amount
- `baseamount_base` (Money) — Amount (Base)
- `extendedamount` (Money) — Extended Amount
- `extendedamount_base` (Money) — Extended Amount (Base)
- `manualdiscountamount` (Money) — Manual Discount Amount
- `manualdiscountamount_base` (Money) — Manual Discount Amount (Base)
- `priceperunit` (Money) — Price Per Unit
- `priceperunit_base` (Money) — Price Per Unit (Base)
- `tax` (Money) — Tax
- `tax_base` (Money) — Tax (Base)
- `volumediscountamount` (Money) — Volume Discount
- `volumediscountamount_base` (Money) — Volume Discount (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `lineitemnumber` (Integer) — Line Item Number
- `quantity` (Decimal) — Quantity
- `sequencenumber` (Integer) — Sequence Number
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number

- Money fields: `manualdiscountamount`, `baseamount_base`, `priceperunit`, `extendedamount`, `priceperunit_base`, `tax_base`, `manualdiscountamount_base`, `volumediscountamount`, `volumediscountamount_base`, `extendedamount_base`, `baseamount`, `tax`
- DateTime fields: `createdon`, `overriddencreatedon`, `modifiedon`
- Status/state/picklist/owner fields: `opportunitystatecode`, `ted_producttypecode`, `ted_saletype`, `producttypecode`, `ownerid`, `skippricecalculation`, `ted_service`, `pricingerrorcode`, `propertyconfigurationstatus`

**Relationships touching approved entities (observed, capped):**
- `lk_opportunityproduct_createdonbehalfby` [ManyToOne] opportunityproduct.createdonbehalfby → systemuser.systemuserid
- `lk_opportunityproduct_entityimage` [ManyToOne] opportunityproduct.entityimageid → imagedescriptor.imagedescriptorid
- `lk_opportunityproduct_modifiedonbehalfby` [ManyToOne] opportunityproduct.modifiedonbehalfby → systemuser.systemuserid
- `lk_opportunityproductbase_createdby` [ManyToOne] opportunityproduct.createdby → systemuser.systemuserid
- `lk_opportunityproductbase_modifiedby` [ManyToOne] opportunityproduct.modifiedby → systemuser.systemuserid
- `new_account_opportunityproduct_Branch` [ManyToOne] opportunityproduct.new_branch → account.accountid
- `new_account_opportunityproduct_customerid` [ManyToOne] opportunityproduct.new_customerid → account.accountid
- `new_account_opportunityproduct_test` [ManyToOne] opportunityproduct.new_test → account.accountid
- `new_te_productflat_opportunityproduct_ExistingFlatProduct` [ManyToOne] opportunityproduct.new_existingflatproduct → te_productflat.te_productflatid
- `opportunity_products` [ManyToOne] opportunityproduct.productid → product.productid
- `OpportunityProduct_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → opportunityproduct.opportunityproductid *(1:N may duplicate root rows)*
- `OpportunityProduct_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → opportunityproduct.opportunityproductid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).
- Line-item aggregates joined to parent headers require either root=line-item entity or future aggregate+link support.

### 12. `campaignactivity` — Campaign Activity

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=73)
- **Recommended approval phase:** Phase 2
- **Currently approved:** No
- **Observed report evidence:** 3 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 4 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=111, relationships=53

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualcost` (Money) — Actual Cost
- `actualcost_base` (Money) — Actual Cost (Base)
- `budgetedcost` (Money) — Budget Allocated
- `budgetedcost_base` (Money) — Budget Allocated (Base)
- `actualdurationminutes` (Integer) — Actual Duration 
- `exchangerate` (Decimal) — Exchange Rate
- `excludeifcontactedinxdays` (Integer) — No. of Days
- `importsequencenumber` (Integer) — Import Sequence Number
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `scheduleddurationminutes` (Integer) — Scheduled Duration
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `actualend` (DateTime) — Actual End
- `actualstart` (DateTime) — Actual Start
- `createdon` (DateTime) — Created On
- `deliverylastattemptedon` (DateTime) — Date Delivery Last Attempted
- `lastonholdtime` (DateTime) — Last On Hold Time
- `modifiedon` (DateTime) — Modified On

- Money fields: `budgetedcost_base`, `actualcost_base`, `actualcost`, `budgetedcost`
- DateTime fields: `createdon`, `overriddencreatedon`, `scheduledstart`, `deliverylastattemptedon`, `actualstart`, `modifiedon`, `postponeactivityprocessinguntil`, `sortdate`, `senton`, `lastonholdtime`, `actualend`, `scheduledend`
- Status/state/picklist/owner fields: `ownerid`, `prioritycode`, `deliveryprioritycode`, `statecode`, `community`, `instancetypecode`, `statuscode`, `channeltypecode`, `typecode`

**Relationships touching approved entities (observed, capped):**
- `activity_pointer_campaignactivity` [ManyToOne] campaignactivity.activityid → activitypointer.activityid
- `bookableresourcebooking_CampaignActivities` [ManyToOne] campaignactivity.regardingobjectid → bookableresourcebooking.bookableresourcebookingid
- `bookableresourcebookingheader_CampaignActivities` [ManyToOne] campaignactivity.regardingobjectid → bookableresourcebookingheader.bookableresourcebookingheaderid
- `business_unit_campaignactivity_activities` [ManyToOne] campaignactivity.owningbusinessunit → businessunit.businessunitid
- `Campaign_CampaignActivities` [ManyToOne] campaignactivity.regardingobjectid → campaign.campaignid
- `campaignactivity_activity_parties` [OneToMany] activityparty.activityid → campaignactivity.activityid *(1:N may duplicate root rows)*
- `campaignactivity_activityparties` [OneToMany] activityparty.partyid → campaignactivity.activityid *(1:N may duplicate root rows)*
- `CampaignActivity_ActivityPointers` [OneToMany] activitypointer.regardingobjectid → campaignactivity.activityid *(1:N may duplicate root rows)*
- `CampaignActivity_Annotation` [OneToMany] annotation.objectid → campaignactivity.activityid *(1:N may duplicate root rows)*
- `CampaignActivity_Appointments` [OneToMany] appointment.regardingobjectid → campaignactivity.activityid *(1:N may duplicate root rows)*
- `CampaignActivity_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → campaignactivity.activityid *(1:N may duplicate root rows)*
- `CampaignActivity_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → campaignactivity.activityid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 13. `task` — Task

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=73)
- **Recommended approval phase:** Phase 2
- **Currently approved:** No
- **Observed report evidence:** 3 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 4 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=131, relationships=95

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualdurationminutes` (Integer) — Duration
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `ldv_closurestatusreason` (Integer) — Closure Status Reason
- `ldv_reminder` (Integer) — Reminder Threshold
- `ldv_sla` (Integer) — SLA Failure Threshold
- `ldv_timelefttoreminder` (Integer) — Time Left To Reminder
- `ldv_timelefttosla` (Integer) — Time Left To Sla
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `percentcomplete` (Integer) — Percent Complete
- `scheduleddurationminutes` (Integer) — Scheduled Duration
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `actualend` (DateTime) — Actual End
- `actualstart` (DateTime) — Actual Start
- `createdon` (DateTime) — Created On
- `lastonholdtime` (DateTime) — Last On Hold Time
- `ldv_remindertargetdate` (DateTime) — Reminder Target DateTime

- Money fields: (none in detail sample)
- DateTime fields: `createdon`, `overriddencreatedon`, `scheduledstart`, `ldv_slatargetdate`, `actualstart`, `modifiedon`, `ldv_slalatestresumedate`, `ldv_remindertargetdate`, `sortdate`, `lastonholdtime`, `actualend`, `scheduledend`
- Status/state/picklist/owner fields: `ownerid`, `prioritycode`, `ldv_slastate`, `statecode`, `ldv_slascope`, `ldv_slastatus`, `statuscode`

**Relationships touching approved entities (observed, capped):**
- `Account_Tasks` [ManyToOne] task.regardingobjectid → account.accountid
- `activity_pointer_task` [ManyToOne] task.activityid → activitypointer.activityid
- `afk_workflowelementoption_Tasks` [ManyToOne] task.regardingobjectid → afk_workflowelementoption.afk_workflowelementoptionid
- `bookableresourcebooking_Tasks` [ManyToOne] task.regardingobjectid → bookableresourcebooking.bookableresourcebookingid
- `bookableresourcebookingheader_Tasks` [ManyToOne] task.regardingobjectid → bookableresourcebookingheader.bookableresourcebookingheaderid
- `business_unit_task_activities` [ManyToOne] task.owningbusinessunit → businessunit.businessunitid
- `Campaign_Tasks` [ManyToOne] task.regardingobjectid → campaign.campaignid
- `CampaignActivity_Tasks` [ManyToOne] task.regardingobjectid → campaignactivity.activityid
- `Contact_Tasks` [ManyToOne] task.regardingobjectid → contact.contactid
- `Contract_Tasks` [ManyToOne] task.regardingobjectid → contract.contractid
- `entitlement_Tasks` [ManyToOne] task.regardingobjectid → entitlement.entitlementid
- `entitlementtemplate_Tasks` [ManyToOne] task.regardingobjectid → entitlementtemplate.entitlementtemplateid

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 14. `activitypointer` — Activity

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=73)
- **Recommended approval phase:** Phase 2
- **Currently approved:** No
- **Observed report evidence:** 1 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 9 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=86, relationships=96

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualdurationminutes` (Integer) — Actual Duration
- `exchangerate` (Decimal) — Exchange Rate
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `scheduleddurationminutes` (Integer) — Scheduled Duration
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `actualend` (DateTime) — Actual End
- `actualstart` (DateTime) — Actual Start
- `createdon` (DateTime) — Date Created
- `deliverylastattemptedon` (DateTime) — Date Delivery Last Attempted
- `lastonholdtime` (DateTime) — Last On Hold Time
- `modifiedon` (DateTime) — Last Updated
- `postponeactivityprocessinguntil` (DateTime) — Delay activity processing until
- `scheduledend` (DateTime) — Due Date
- `scheduledstart` (DateTime) — Start Date
- `senton` (DateTime) — Date Sent
- `sortdate` (DateTime) — Sort Date
- `community` (Picklist) — Social Channel options=[1:Facebook; 2:Twitter; 0:Other]

- Money fields: (none in detail sample)
- DateTime fields: `createdon`, `deliverylastattemptedon`, `scheduledstart`, `actualstart`, `modifiedon`, `postponeactivityprocessinguntil`, `sortdate`, `senton`, `lastonholdtime`, `actualend`, `scheduledend`
- Status/state/picklist/owner fields: `instancetypecode`, `prioritycode`, `deliveryprioritycode`, `statecode`, `community`, `ownerid`, `statuscode`

**Relationships touching approved entities (observed, capped):**
- `Account_ActivityPointers` [ManyToOne] activitypointer.regardingobjectid → account.accountid
- `activity_campaignresponse` [OneToMany] campaignresponse.originatingactivityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_activity_mime_attachment` [OneToMany] activitymimeattachment.objectid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_appointment` [OneToMany] appointment.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_BulkOperation` [OneToMany] bulkoperation.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_BulkOperation_logs` [OneToMany] bulkoperationlog.bulkoperationid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_campaignactivity` [OneToMany] campaignactivity.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_campaignresponse` [OneToMany] campaignresponse.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_email` [OneToMany] email.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_email_hash` [OneToMany] emailhash.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_fax` [OneToMany] fax.activityid → activitypointer.activityid *(1:N may duplicate root rows)*
- `activity_pointer_incident_resolution` [OneToMany] incidentresolution.activityid → activitypointer.activityid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 15. `quote` — Quotes

- **Standard/Custom:** Standard
- **Reporting priority:** High (score=72)
- **Recommended approval phase:** Phase 2
- **Currently approved:** No
- **Observed report evidence:** 2 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 6 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=195, relationships=66

**Business use cases (recommendations):**
- Count of quotes created last 8 calendar months — feasibility: Supported: count/aggregate + dateRange
- Quotes with customer account — feasibility: Supported: list + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `discountamount` (Money) — Quote Discount Amount
- `discountamount_base` (Money) — Quote Discount Amount (Base)
- `freightamount` (Money) — Shipping and Handling
- `freightamount_base` (Money) — Freight Amount (Base)
- `totalamount` (Money) — Grand Total
- `totalamount_base` (Money) — Total Amount (Base)
- `totalamountlessfreight` (Money) — Total Pre-Freight Amount
- `totalamountlessfreight_base` (Money) — Total Pre-Freight Amount (Base)
- `totaldiscountamount` (Money) — Total Discount Amount
- `totaldiscountamount_base` (Money) — Total Discount Amount (Base)
- `totallineitemamount` (Money) — Total Price
- `totallineitemamount_base` (Money) — Total Detail Amount (Base)
- `totallineitemdiscountamount` (Money) — Total Line Item Discount Amount
- `totallineitemdiscountamount_base` (Money) — Total Line Item Discount Amount (Base)
- `totaltax` (Money) — Tax
- `totaltax_base` (Money) — Total Tax (Base)
- `discountpercentage` (Decimal) — Quote Discount (%)
- `exchangerate` (Decimal) — Exchange Rate

- Money fields: `totallineitemamount_base`, `freightamount_base`, `totallineitemdiscountamount_base`, `totaldiscountamount`, `discountamount_base`, `freightamount`, `totalamountlessfreight_base`, `totalamountlessfreight`, `totallineitemamount`, `totaldiscountamount_base`, `totallineitemdiscountamount`, `totalamount`, `totalamount_base`, `totaltax`, `discountamount`, `totaltax_base`
- DateTime fields: `closedon`, `modifiedon`, `overriddencreatedon`, `new_lineitems_date`, `createdon`, `new_dateapproved`, `effectiveto`, `requestdeliveryby`, `expireson`, `lastonholdtime`, `new_customerpostartdate`, `effectivefrom`
- Status/state/picklist/owner fields: `freighttermscode`, `paymenttermscode`, `statuscode`, `skippricecalculation`, `shipto_freighttermscode`, `new_customerpoterm`, `shippingmethodcode`, `new_offertype`, `ownerid`, `pricingerrorcode`, `statecode`

**Relationships touching approved entities (observed, capped):**
- `business_unit_quotes` [ManyToOne] quote.owningbusinessunit → businessunit.businessunitid
- `campaign_quotes` [ManyToOne] quote.campaignid → campaign.campaignid
- `lk_opportunitysalesprocess_quoteid` [OneToMany] opportunitysalesprocess.quoteid → quote.quoteid *(1:N may duplicate root rows)*
- `lk_quote_createdonbehalfby` [ManyToOne] quote.createdonbehalfby → systemuser.systemuserid
- `lk_quote_modifiedonbehalfby` [ManyToOne] quote.modifiedonbehalfby → systemuser.systemuserid
- `lk_quotebase_createdby` [ManyToOne] quote.createdby → systemuser.systemuserid
- `lk_quotebase_modifiedby` [ManyToOne] quote.modifiedby → systemuser.systemuserid
- `manualsla_quote` [ManyToOne] quote.slaid → sla.slaid
- `msdyn_playbookinstance_quote` [OneToMany] msdyn_playbookinstance.regarding → quote.quoteid *(1:N may duplicate root rows)*
- `new_account_quote_CustomerName` [ManyToOne] quote.new_customername → account.accountid
- `new_contact_quote_BillingContact` [ManyToOne] quote.new_billingcontact → contact.contactid
- `new_contact_quote_Contactname` [ManyToOne] quote.new_contactname → contact.contactid

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 16. `competitor` — Competitor

- **Standard/Custom:** Standard
- **Reporting priority:** Medium (score=68)
- **Recommended approval phase:** Phase 3
- **Currently approved:** No
- **Observed report evidence:** 2 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 3 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** OrganizationOwned; Advanced Find valid: True
- **Counts (observed):** attributes=97, relationships=27

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `reportedrevenue` (Money) — Reported Revenue
- `reportedrevenue_base` (Money) — Reported Revenue (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `reportingquarter` (Integer) — Reporting Quarter
- `reportingyear` (Integer) — Reporting Year
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `winpercentage` (Double) — Win Percentage
- `createdon` (DateTime) — Created On
- `modifiedon` (DateTime) — Modified On
- `overriddencreatedon` (DateTime) — Record Created On
- `createdby` (Lookup) — Created By targets=[systemuser]
- `createdonbehalfby` (Lookup) — Created By (Delegate) targets=[systemuser]
- `modifiedby` (Lookup) — Modified By targets=[systemuser]
- `modifiedonbehalfby` (Lookup) — Modified By (Delegate) targets=[systemuser]
- `organizationid` (Lookup) — Organization targets=[organization]
- `transactioncurrencyid` (Lookup) — Currency targets=[transactioncurrency]

- Money fields: `reportedrevenue_base`, `reportedrevenue`
- DateTime fields: `createdon`, `modifiedon`, `overriddencreatedon`
- Status/state/picklist/owner fields: `address2_shippingmethodcode`, `address2_addresstypecode`, `address1_addresstypecode`, `address1_shippingmethodcode`

**Relationships touching approved entities (observed, capped):**
- `competitor_addresses` [OneToMany] competitoraddress.parentid → competitor.competitorid *(1:N may duplicate root rows)*
- `Competitor_Annotation` [OneToMany] annotation.objectid → competitor.competitorid *(1:N may duplicate root rows)*
- `Competitor_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → competitor.competitorid *(1:N may duplicate root rows)*
- `Competitor_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → competitor.competitorid *(1:N may duplicate root rows)*
- `competitor_connections1` [OneToMany] connection.record1id → competitor.competitorid *(1:N may duplicate root rows)*
- `competitor_connections2` [OneToMany] connection.record2id → competitor.competitorid *(1:N may duplicate root rows)*
- `Competitor_DuplicateBaseRecord` [OneToMany] duplicaterecord.baserecordid → competitor.competitorid *(1:N may duplicate root rows)*
- `Competitor_DuplicateMatchingRecord` [OneToMany] duplicaterecord.duplicaterecordid → competitor.competitorid *(1:N may duplicate root rows)*
- `competitor_MailboxTrackingFolders` [OneToMany] mailboxtrackingfolder.regardingobjectid → competitor.competitorid *(1:N may duplicate root rows)*
- `competitor_opportunity_activities` [OneToMany] opportunityclose.competitorid → competitor.competitorid *(1:N may duplicate root rows)*
- `competitor_PostFollows` [OneToMany] postfollow.regardingobjectid → competitor.competitorid *(1:N may duplicate root rows)*
- `competitor_PostRegardings` [OneToMany] postregarding.regardingobjectid → competitor.competitorid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 17. `list` — Marketing List

- **Standard/Custom:** Standard
- **Reporting priority:** Medium (score=67)
- **Recommended approval phase:** Phase 3
- **Currently approved:** No
- **Observed report evidence:** 0 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 8 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=59, relationships=24

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `cost` (Money) — Cost
- `cost_base` (Money) — Cost (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `membercount` (Integer) — Members Count
- `membertype` (Integer) — Member Type
- `processedmembercount` (Integer) — processedMemberCount
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `createdon` (DateTime) — Created On
- `lastusedon` (DateTime) — Last Used On
- `modifiedon` (DateTime) — Modified On
- `overriddencreatedon` (DateTime) — Record Created On
- `createdby` (Lookup) — Created By targets=[systemuser]
- `createdfromcode` (Picklist) — Marketing List Member Type options=[1:Account; 2:Contact; 4:Lead]
- `createdonbehalfby` (Lookup) — Created By (Delegate) targets=[systemuser]
- `modifiedby` (Lookup) — Modified By targets=[systemuser]
- `modifiedonbehalfby` (Lookup) — Modified By (Delegate) targets=[systemuser]

- Money fields: `cost_base`, `cost`
- DateTime fields: `lastusedon`, `createdon`, `overriddencreatedon`, `modifiedon`
- Status/state/picklist/owner fields: `createdfromcode`, `statecode`, `ownerid`, `statuscode`

**Relationships touching approved entities (observed, capped):**
- `business_unit_list` [ManyToOne] list.owningbusinessunit → businessunit.businessunitid
- `List_Annotation` [OneToMany] annotation.objectid → list.listid *(1:N may duplicate root rows)*
- `List_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → list.listid *(1:N may duplicate root rows)*
- `List_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → list.listid *(1:N may duplicate root rows)*
- `List_BulkOperations` [OneToMany] bulkoperation.regardingobjectid → list.listid *(1:N may duplicate root rows)*
- `list_connections1` [OneToMany] connection.record1id → list.listid *(1:N may duplicate root rows)*
- `list_connections2` [OneToMany] connection.record2id → list.listid *(1:N may duplicate root rows)*
- `List_DuplicateBaseRecord` [OneToMany] duplicaterecord.baserecordid → list.listid *(1:N may duplicate root rows)*
- `List_DuplicateMatchingRecord` [OneToMany] duplicaterecord.duplicaterecordid → list.listid *(1:N may duplicate root rows)*
- `list_listoperation_ListId` [OneToMany] listoperation.listid → list.listid *(1:N may duplicate root rows)*
- `list_MailboxTrackingFolders` [OneToMany] mailboxtrackingfolder.regardingobjectid → list.listid *(1:N may duplicate root rows)*
- `list_principalobjectattributeaccess` [OneToMany] principalobjectattributeaccess.objectid → list.listid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 18. `email` — Email

- **Standard/Custom:** Standard
- **Reporting priority:** Medium (score=66)
- **Recommended approval phase:** Phase 3
- **Currently approved:** No
- **Observed report evidence:** 0 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 7 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=148, relationships=97

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualdurationminutes` (Integer) — Duration
- `attachmentcount` (Integer) — Attachment Count
- `attachmentopencount` (Integer) — Attachment Open Count
- `baseconversationindexhash` (Integer) — Conversation Index (Hash)
- `deliveryattempts` (Integer) — No. of Delivery Attempts
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number
- `isunsafe` (Integer) — IsUnsafe
- `linksclickedcount` (Integer) — Links Clicked Count
- `onholdtime` (Integer) — On Hold Time (Minutes)
- `opencount` (Integer) — Open Count
- `replycount` (Integer) — Reply Count
- `scheduleddurationminutes` (Integer) — Scheduled Duration
- `timezoneruleversionnumber` (Integer) — Time Zone Rule Version Number
- `versionnumber` (BigInt) — Version Number
- `actualend` (DateTime) — Actual End
- `actualstart` (DateTime) — Actual Start
- `createdon` (DateTime) — Created On

- Money fields: (none in detail sample)
- DateTime fields: `scheduledend`, `modifiedon`, `scheduledstart`, `overriddencreatedon`, `senton`, `delayedemailsendtime`, `lastopenedtime`, `createdon`, `actualstart`, `sortdate`, `actualend`, `postponeemailprocessinguntil`
- Status/state/picklist/owner fields: `notifications`, `statuscode`, `emailreminderstatus`, `emailremindertype`, `deliveryprioritycode`, `ownerid`, `prioritycode`, `correlationmethod`, `statecode`

**Relationships touching approved entities (observed, capped):**
- `Account_Email_EmailSender` [ManyToOne] email.emailsender → account.accountid
- `Account_Email_SendersAccount` [ManyToOne] email.sendersaccount → account.accountid
- `Account_Emails` [ManyToOne] email.regardingobjectid → account.accountid
- `activity_pointer_email` [ManyToOne] email.activityid → activitypointer.activityid
- `afk_workflowelementoption_Emails` [ManyToOne] email.regardingobjectid → afk_workflowelementoption.afk_workflowelementoptionid
- `AsyncOperation_Emails` [ManyToOne] email.regardingobjectid → asyncoperation.asyncoperationid
- `bookableresourcebooking_Emails` [ManyToOne] email.regardingobjectid → bookableresourcebooking.bookableresourcebookingid
- `bookableresourcebookingheader_Emails` [ManyToOne] email.regardingobjectid → bookableresourcebookingheader.bookableresourcebookingheaderid
- `BulkOperation_Email` [ManyToOne] email.regardingobjectid → bulkoperation.activityid
- `business_unit_email_activities` [ManyToOne] email.owningbusinessunit → businessunit.businessunitid
- `Campaign_Emails` [ManyToOne] email.regardingobjectid → campaign.campaignid
- `CampaignActivity_Emails` [ManyToOne] email.regardingobjectid → campaignactivity.activityid

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

### 19. `quotedetail` — Quote Product

- **Standard/Custom:** Standard
- **Reporting priority:** Medium (score=64)
- **Recommended approval phase:** Phase 3
- **Currently approved:** No
- **Observed report evidence:** 0 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 6 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=112, relationships=27

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `baseamount` (Money) — Amount
- `baseamount_base` (Money) — Amount (Base)
- `extendedamount` (Money) — Extended Amount
- `extendedamount_base` (Money) — Extended Amount (Base)
- `manualdiscountamount` (Money) — Manual Discount
- `manualdiscountamount_base` (Money) — Manual Discount (Base)
- `priceperunit` (Money) — Price Per Unit
- `priceperunit_base` (Money) — Price Per Unit (Base)
- `tax` (Money) — Tax
- `tax_base` (Money) — Tax (Base)
- `ted_nrcsalesprice` (Money) — NRC Sales price
- `ted_nrcsalesprice_base` (Money) — NRC Sales price (Base)
- `ted_rcsalesprice` (Money) — RC Sales price
- `ted_rcsalesprice_base` (Money) — RC Sales price (Base)
- `volumediscountamount` (Money) — Volume Discount
- `volumediscountamount_base` (Money) — Volume Discount (Base)
- `exchangerate` (Decimal) — Exchange Rate
- `importsequencenumber` (Integer) — Import Sequence Number

- Money fields: `manualdiscountamount`, `ted_rcsalesprice_base`, `ted_rcsalesprice`, `baseamount`, `baseamount_base`, `priceperunit`, `priceperunit_base`, `tax_base`, `ted_nrcsalesprice_base`, `ted_nrcsalesprice`, `manualdiscountamount_base`, `volumediscountamount`, `volumediscountamount_base`, `extendedamount_base`, `extendedamount`, `tax`
- DateTime fields: `createdon`, `overriddencreatedon`, `modifiedon`, `requestdeliveryby`
- Status/state/picklist/owner fields: `quotestatecode`, `ted_saletype`, `shipto_freighttermscode`, `ted_producttypecode`, `skippricecalculation`, `producttypecode`, `ownerid`, `ted_service`, `pricingerrorcode`, `propertyconfigurationstatus`

**Relationships touching approved entities (observed, capped):**
- `lk_quotedetail_createdonbehalfby` [ManyToOne] quotedetail.createdonbehalfby → systemuser.systemuserid
- `lk_quotedetail_modifiedonbehalfby` [ManyToOne] quotedetail.modifiedonbehalfby → systemuser.systemuserid
- `lk_quotedetailbase_createdby` [ManyToOne] quotedetail.createdby → systemuser.systemuserid
- `lk_quotedetailbase_modifiedby` [ManyToOne] quotedetail.modifiedby → systemuser.systemuserid
- `msdyn_quotedetail_salesorderdetail` [OneToMany] salesorderdetail.quotedetailid → quotedetail.quotedetailid *(1:N may duplicate root rows)*
- `new_account_quotedetail_Branch` [ManyToOne] quotedetail.new_branch → account.accountid
- `product_quote_details` [ManyToOne] quotedetail.productid → product.productid
- `productAssociation_quote_details` [ManyToOne] quotedetail.productassociationid → productassociation.productassociationid
- `quote_details` [ManyToOne] quotedetail.quoteid → quote.quoteid
- `QuoteDetail_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → quotedetail.quotedetailid *(1:N may duplicate root rows)*
- `QuoteDetail_BulkDeleteFailures` [OneToMany] bulkdeletefailure.regardingobjectid → quotedetail.quotedetailid *(1:N may duplicate root rows)*
- `QuoteDetail_Dynamicpropertyinstance` [OneToMany] dynamicpropertyinstance.regardingobjectid → quotedetail.quotedetailid *(1:N may duplicate root rows)*

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).
- Line-item aggregates joined to parent headers require either root=line-item entity or future aggregate+link support.

### 20. `goal` — Goal

- **Standard/Custom:** Standard
- **Reporting priority:** Medium (score=63)
- **Recommended approval phase:** Phase 3
- **Currently approved:** No
- **Observed report evidence:** 4 reportentities link row(s) (entity logical name references)
- **Observed view evidence:** 8 savedquery/userquery row(s) with matching ReturnedTypeCode
- **Ownership:** UserOwned; Advanced Find valid: True
- **Counts (observed):** attributes=120, relationships=36

**Business use cases (recommendations):**
- List recent records — feasibility: Supported when approved: list + sort + date
- Count records in a calendar period — feasibility: Supported: count/aggregate + dateRange
- Aggregate with related entity attributes — feasibility: Unsupported: aggregate + linksJson

**Relevant fields (observed sample of reporting-useful attributes):**
- `actualmoney` (Money) — Actual (Money)
- `actualmoney_base` (Money) — Actual (Money) (Base)
- `computedtargetasoftodaymoney` (Money) — Today's Target (Money)
- `computedtargetasoftodaymoney_base` (Money) — Today's Target (Money) (Base)
- `customrollupfieldmoney` (Money) — Custom Rollup Field (Money)
- `customrollupfieldmoney_base` (Money) — Custom Rollup Field (Money) (Base)
- `inprogressmoney` (Money) — In-progress (Money)
- `inprogressmoney_base` (Money) — In-progress (Money) (Base)
- `stretchtargetmoney` (Money) — Stretch Target (Money)
- `stretchtargetmoney_base` (Money) — Stretch Target (Money) (Base)
- `targetmoney` (Money) — Target (Money)
- `targetmoney_base` (Money) — Target (Money) (Base)
- `actualdecimal` (Decimal) — Actual (Decimal)
- `actualinteger` (Integer) — Actual (Integer)
- `computedtargetasoftodaydecimal` (Decimal) — Today's Target (Decimal)
- `computedtargetasoftodayinteger` (Integer) — Today's Target (Integer)
- `computedtargetasoftodaypercentageachieved` (Decimal) — Today's Target (Percentage Achieved)
- `customrollupfielddecimal` (Decimal) — Custom Rollup Field (Decimal)

- Money fields: `inprogressmoney_base`, `actualmoney_base`, `actualmoney`, `computedtargetasoftodaymoney`, `computedtargetasoftodaymoney_base`, `customrollupfieldmoney_base`, `targetmoney_base`, `customrollupfieldmoney`, `stretchtargetmoney`, `inprogressmoney`, `stretchtargetmoney_base`, `targetmoney`
- DateTime fields: `createdon`, `modifiedon`, `lastrolledupdate`, `overriddencreatedon`, `goalstartdate`, `goalenddate`
- Status/state/picklist/owner fields: `ownerid`, `fiscalperiod`, `statecode`, `amountdatatype`, `fiscalyear`, `statuscode`

**Relationships touching approved entities (observed, capped):**
- `business_unit_goal` [ManyToOne] goal.owningbusinessunit → businessunit.businessunitid
- `Goal_Annotation` [OneToMany] annotation.objectid → goal.goalid *(1:N may duplicate root rows)*
- `Goal_AsyncOperations` [OneToMany] asyncoperation.regardingobjectid → goal.goalid *(1:N may duplicate root rows)*
- `goal_connections1` [OneToMany] connection.record1id → goal.goalid *(1:N may duplicate root rows)*
- `goal_connections2` [OneToMany] connection.record2id → goal.goalid *(1:N may duplicate root rows)*
- `Goal_DuplicateBaseRecord` [OneToMany] duplicaterecord.baserecordid → goal.goalid *(1:N may duplicate root rows)*
- `Goal_DuplicateMatchingRecord` [OneToMany] duplicaterecord.duplicaterecordid → goal.goalid *(1:N may duplicate root rows)*
- `goal_parent_goal` [ManyToOne] goal.parentgoalid → goal.goalid
- `goal_parent_goal` [OneToMany] goal.parentgoalid → goal.goalid *(1:N may duplicate root rows)*
- `goal_principalobjectattributeaccess` [OneToMany] principalobjectattributeaccess.objectid → goal.goalid *(1:N may duplicate root rows)*
- `Goal_ProcessSessions` [OneToMany] processsession.regardingobjectid → goal.goalid *(1:N may duplicate root rows)*
- `Goal_RollupError_Goal` [ManyToOne] goal.goalwitherrorid → goal.goalid

**Query engine limitations for this entity:**
- List/Count/Aggregate/GroupBy/Date/Sort: supported on root when entity is approved and metadata validates.
- Single-hop / two-hop links: supported for list/count only.
- **Aggregate + linksJson: unsupported** (current engine).

## Additional high-scoring entities (not in detailed top 20)

- `appointment` (Appointment) score=61, reports=0, views=4, custom=False
- `campaignresponse` (Campaign Response) score=61, reports=0, views=4, custom=False
- `pricelevel` (Price List) score=61, reports=0, views=4, custom=False
- `invoicedetail` (Invoice Product) score=60, reports=0, views=3, custom=False
- `phonecall` (Phone Call) score=60, reports=0, views=3, custom=False
- `businessunit` (Business Unit) score=58, reports=0, views=2, custom=False
- `salesorder` (Commercial Order) score=58, reports=3, views=7, custom=False
- `queueitem` (Queue Item) score=56, reports=8, views=16, custom=False
- `te_serviceorder` (Service Order) score=52, reports=3, views=17, custom=True
- `contract` (Old Installed Base) score=46, reports=0, views=7, custom=False
- `metric` (Goal Metric) score=40, reports=0, views=3, custom=False
- `transactioncurrency` (Currency) score=40, reports=0, views=3, custom=False
- `uomschedule` (Unit Group) score=40, reports=0, views=3, custom=False
- `entitlement` (Entitlement) score=38, reports=0, views=2, custom=False
- `te_resourceregistry` (Resource Registry) score=33, reports=1, views=10, custom=True

## Suggested approval rollout

**Observed custom WE entities with report/view evidence (not auto-approved):**
- `te_serviceorder` (Service Order) — 3 report link(s), 17 views, 828 attributes
- `te_resourceregistry` (Resource Registry) — 1 report link, 10 views
- `ted_installedbase` — 10 views (custom installed-base reporting candidate)
- Plus **108** total `te_` / `ted_` entities in inventory — evaluate individually; names alone are not sufficient evidence of reporting value.

1. **Phase 1 (high value, low ambiguity):** `opportunity`, `quote`, `invoice`, `salesorderdetail`, `product`, `systemuser`, `businessunit` (keep existing `account`/`contact`/`lead`/`salesorder`)
2. **Phase 2 (pipeline / marketing depth):** `quotedetail`, `invoicedetail`, `opportunityproduct`, `campaign`, `campaignactivity` (+ `campaignresponse` if used)
3. **Phase 3 (custom WE):** start with `te_serviceorder`, then `ted_installedbase` / `te_resourceregistry` after field-level review
4. **Defer / caution:** `incident`/`queueitem` (service desk; valuable but outside core Sales/Marketing unless management wants omnichannel), `goal` (needs metric setup), activity entities unless activity reporting is in scope

**Engine gap to plan before rich cross-entity KPIs:** implement `aggregate` + `linksJson` (currently unsupported).

## Files

- `crm-entity-inventory.json` — full compact entity inventory
- `crm-report-inventory.json` — reports/views inventory + safe entity references
- `crm-report-entity-recommendations.md` — this ranked shortlist

