namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Offline/mock seed for account, contact, salesorder — used when live discovery is unavailable.
/// Field names match verified ECRMBSS / standard Dynamics patterns.
/// </summary>
public static class StaticSeedCrmMetadata
{
    public static IReadOnlyList<CrmEntityMetadata> CreateApprovedSeeds(EntityApprovalPolicy policy)
    {
        return
        [
            BuildAccount(policy),
            BuildContact(policy),
            BuildSalesOrder(policy)
        ];
    }

    private static CrmEntityMetadata BuildAccount(EntityApprovalPolicy policy) =>
        new()
        {
            LogicalName = "account",
            EntitySetName = "accounts",
            DisplayName = "Account",
            PrimaryIdAttribute = "accountid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = policy.IsApproved("account"),
            Aliases = policy.GetAliases("account"),
            Attributes =
            [
                Attr("accountid", CrmAttributeKind.Uniqueidentifier, filterable: true, sortable: false),
                Attr("name", CrmAttributeKind.String, filterable: true, sortable: true, searchable: true),
                Attr("accountnumber", CrmAttributeKind.String, filterable: true, searchable: true),
                Attr("telephone1", CrmAttributeKind.String, filterable: true),
                Attr("emailaddress1", CrmAttributeKind.String, filterable: true),
                Attr("address1_city", CrmAttributeKind.String, filterable: true),
                Attr("address1_country", CrmAttributeKind.String, filterable: true),
                Attr("createdon", CrmAttributeKind.DateTime, filterable: true, sortable: true),
                Attr("modifiedon", CrmAttributeKind.DateTime, filterable: true, sortable: false)
            ],
            Relationships =
            [
                new CrmRelationshipMetadata
                {
                    SchemaName = "contact_customer_accounts",
                    Kind = CrmRelationshipKind.OneToMany,
                    ReferencingEntity = "contact",
                    ReferencedEntity = "account",
                    ReferencingAttribute = "parentcustomerid",
                    ReferencedAttribute = "accountid",
                    ReferencedEntityNavigationPropertyName = "contact_customer_accounts",
                    IsApproved = true
                }
            ]
        };

    private static CrmEntityMetadata BuildContact(EntityApprovalPolicy policy) =>
        new()
        {
            LogicalName = "contact",
            EntitySetName = "contacts",
            DisplayName = "Contact",
            PrimaryIdAttribute = "contactid",
            PrimaryNameAttribute = "fullname",
            IsApprovedForAi = policy.IsApproved("contact"),
            Aliases = policy.GetAliases("contact"),
            Attributes =
            [
                Attr("contactid", CrmAttributeKind.Uniqueidentifier, filterable: true),
                Attr("fullname", CrmAttributeKind.String, filterable: true, sortable: true, searchable: true),
                Attr("firstname", CrmAttributeKind.String, filterable: true, searchable: true),
                Attr("lastname", CrmAttributeKind.String, filterable: true, searchable: true),
                Attr("emailaddress1", CrmAttributeKind.String, filterable: true),
                Attr("telephone1", CrmAttributeKind.String, filterable: true),
                Attr("jobtitle", CrmAttributeKind.String, filterable: true),
                Attr("parentcustomerid", CrmAttributeKind.Customer, filterable: true,
                    odata: "_parentcustomerid_value", aliases: ["accountid"]),
                Attr("createdon", CrmAttributeKind.DateTime, filterable: true, sortable: true)
            ],
            Relationships =
            [
                new CrmRelationshipMetadata
                {
                    SchemaName = "contact_customer_accounts",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "contact",
                    ReferencedEntity = "account",
                    ReferencingAttribute = "parentcustomerid",
                    ReferencedAttribute = "accountid",
                    ReferencingEntityNavigationPropertyName = "parentcustomerid_account",
                    IsApproved = true
                }
            ]
        };

    private static CrmEntityMetadata BuildSalesOrder(EntityApprovalPolicy policy)
    {
        var status = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["New"] = 1, ["Pending"] = 2, ["In Progress"] = 3, ["No Money"] = 4,
            ["Complete"] = 100001, ["Partial"] = 100002, ["Invoiced"] = 100003
        };
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Active"] = 0, ["Submitted"] = 1, ["Canceled"] = 2, ["Fulfilled"] = 3, ["Invoiced"] = 4
        };

        return new CrmEntityMetadata
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            DisplayName = "Sales Order",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = policy.IsApproved("salesorder"),
            Aliases = policy.GetAliases("salesorder"),
            Attributes =
            [
                Attr("salesorderid", CrmAttributeKind.Uniqueidentifier, filterable: true),
                Attr("name", CrmAttributeKind.String, filterable: true, searchable: true),
                Attr("ordernumber", CrmAttributeKind.String, filterable: true, searchable: true),
                Attr("createdon", CrmAttributeKind.DateTime, filterable: true, sortable: true),
                Attr("modifiedon", CrmAttributeKind.DateTime, filterable: true),
                Attr("totalamount", CrmAttributeKind.Money, filterable: true, sortable: true),
                Attr("statuscode", CrmAttributeKind.Status, filterable: true, options: status),
                Attr("statecode", CrmAttributeKind.State, filterable: true, options: state),
                Attr("te_customeraccountid", CrmAttributeKind.Lookup, filterable: true,
                    odata: "_te_customeraccountid_value"),
                Attr("customerid", CrmAttributeKind.Customer, filterable: true, odata: "_customerid_value")
            ],
            Relationships =
            [
                new CrmRelationshipMetadata
                {
                    SchemaName = "order_customer_accounts",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "salesorder",
                    ReferencedEntity = "account",
                    ReferencingAttribute = "customerid",
                    ReferencedAttribute = "accountid",
                    IsApproved = true
                }
            ]
        };
    }

    private static CrmAttributeMetadata Attr(
        string logicalName,
        CrmAttributeKind kind,
        bool filterable = false,
        bool sortable = false,
        bool searchable = false,
        string? odata = null,
        IReadOnlyList<string>? aliases = null,
        Dictionary<string, int>? options = null)
    {
        var byLabel = options ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byValue = byLabel.ToDictionary(kv => kv.Value, kv => kv.Key);
        return new CrmAttributeMetadata
        {
            LogicalName = logicalName,
            ODataPropertyName = odata ?? AttributeKindMapper.ToODataPropertyName(logicalName, kind),
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = filterable || AttributeKindMapper.IsTypicallyFilterable(kind),
            IsSortable = sortable || (filterable && AttributeKindMapper.IsTypicallySortable(kind) && kind == CrmAttributeKind.DateTime),
            IsSearchable = searchable || AttributeKindMapper.IsSearchable(kind),
            AllowedOperators = AttributeKindMapper.DefaultOperators(kind),
            Aliases = aliases ?? Array.Empty<string>(),
            OptionSetByLabel = byLabel,
            OptionSetByValue = byValue
        };
    }
}
