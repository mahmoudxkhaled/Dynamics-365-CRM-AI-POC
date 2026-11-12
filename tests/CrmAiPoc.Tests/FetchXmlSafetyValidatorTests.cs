using System.Collections.Concurrent;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Mocked unit tests for LLM-generated FetchXML validation (no live CRM calls).
/// </summary>
public sealed class FetchXmlSafetyValidatorTests
{
    [Fact]
    public async Task Accepts_Simple_List_FetchXml()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <attribute name="ordernumber" />
                <attribute name="createdon" />
                <order attribute="createdon" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal("salesorder", outcome.Query!.EntityLogicalName);
        Assert.False(outcome.Query.IsAggregate);
    }

    [Fact]
    public async Task Accepts_Count_FetchXml()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="salesorderid" alias="cnt" aggregate="count" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.True(outcome.Query!.IsAggregate);
        Assert.True(outcome.Query.IsCountOnly);
    }

    [Fact]
    public async Task Accepts_Native_Sum_Aggregation()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Contains(outcome.Query!.Columns, c => c.Key == "revenue");
    }

    [Fact]
    public async Task Accepts_Monthly_Grouping()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="createdon" alias="year" groupby="true" dategrouping="year" />
                <attribute name="createdon" alias="month" groupby="true" dategrouping="month" />
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Accepts_And_Or_Filters()
    {
        var outcome = await ValidateAsync("""
            <fetch top="10">
              <entity name="account">
                <attribute name="name" />
                <filter type="and">
                  <condition attribute="name" operator="not-null" />
                  <filter type="or">
                    <condition attribute="telephone1" operator="not-null" />
                    <condition attribute="emailaddress1" operator="not-null" />
                  </filter>
                </filter>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Accepts_Inner_And_Outer_Joins()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <link-entity name="account" from="accountid" to="customerid" alias="customer" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="name" />
                </link-entity>
                <link-entity name="contact" from="contactid" to="primarycontactid" alias="buyer" link-type="outer" relationship="salesorder_primarycontact">
                  <attribute name="fullname" />
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Accepts_Nested_Relationships()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <link-entity name="account" from="accountid" to="customerid" alias="customer" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="name" />
                  <link-entity name="contact" from="contactid" to="primarycontactid" alias="pc" link-type="outer" relationship="account_primary_contact">
                    <attribute name="fullname" />
                  </link-entity>
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Accepts_Null_Conditions_And_Anti_Join()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="account">
                <attribute name="name" />
                <filter type="and">
                  <condition attribute="primarycontactid" operator="null" />
                  <condition attribute="telephone1" operator="not-null" />
                </filter>
                <link-entity name="contact" from="contactid" to="primarycontactid" alias="pc" link-type="outer">
                  <attribute name="fullname" />
                  <filter type="and">
                    <condition attribute="contactid" operator="null" />
                  </filter>
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_Unknown_Entity()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="email">
                <attribute name="subject" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("ENTITY_NOT_APPROVED", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Unknown_Attribute()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="invented_field" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_FIELD", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Unreadable_Attribute()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="secretnote" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_FIELD", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Invalid_Relationship()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <link-entity name="account" from="accountid" to="customerid" alias="customer" link-type="inner" relationship="invented_relationship_schema">
                  <attribute name="name" />
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_RELATIONSHIP", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Invalid_Alias_Duplicate()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <link-entity name="account" from="accountid" to="customerid" alias="customer" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="name" />
                </link-entity>
                <link-entity name="contact" from="contactid" to="primarycontactid" alias="customer" link-type="outer" relationship="salesorder_primarycontact">
                  <attribute name="fullname" />
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_ALIAS", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Unsafe_Dtd_And_Invalid_Xml()
    {
        var badXml = await ValidateAsync("""<!DOCTYPE fetch [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><fetch><entity name="account">&xxe;</entity></fetch>""");
        Assert.False(badXml.Success);

        var malformed = await ValidateAsync("<fetch><entity name='account'></fetch>");
        Assert.False(malformed.Success);
        Assert.Equal("INVALID_XML", malformed.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Unsupported_Elements()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="account">
                <attribute name="name" />
                <script>alert(1)</script>
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("UNSUPPORTED_FETCHXML", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rejects_Excessive_Top()
    {
        var outcome = await ValidateAsync("""
            <fetch top="50000">
              <entity name="account">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("QUERY_TOO_LARGE", outcome.ErrorCode);
    }

    [Fact]
    public async Task Accepts_Normal_List_Sorting_By_Attribute()
    {
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <attribute name="totalamount" />
                <order attribute="totalamount" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Accepts_Aggregate_Sorting_By_Declared_Alias()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="createdon" alias="year" groupby="true" dategrouping="year" />
                <attribute name="createdon" alias="month" groupby="true" dategrouping="month" />
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <order alias="revenue" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_Undefined_Aggregate_Sort_Alias()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <order alias="missing_alias" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_SORT", outcome.ErrorCode);
        Assert.Contains("not declared", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_Aggregate_Alias_Stuffed_Into_Order_Attribute()
    {
        // Case A pattern: declared alias totalrevenue incorrectly referenced as order attribute=.
        var outcome = await ValidateAsync("""
            <fetch aggregate="true" top="5">
              <entity name="salesorder">
                <attribute name="name" alias="product" groupby="true" />
                <attribute name="totalamount" alias="totalrevenue" aggregate="sum" />
                <order attribute="totalrevenue" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_SORT", outcome.ErrorCode);
        Assert.Contains("alias=\"totalrevenue\"", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not attribute", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_Aggregate_Sort_By_Physical_Attribute_Before_Crm()
    {
        // Case B pattern: order attribute="totalamount" instead of alias="revenue".
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="createdon" alias="month" groupby="true" dategrouping="month" />
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <order attribute="totalamount" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_SORT", outcome.ErrorCode);
        Assert.Contains("alias=\"revenue\"", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_Without_Top_Or_Count_Applies_Default_Top()
    {
        var outcome = await ValidateAsync("""
            <fetch>
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(FetchXmlSafetyLimits.DefaultTop, outcome.Query!.Top);
        Assert.Contains($"top=\"{FetchXmlSafetyLimits.DefaultTop}\"", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count=", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fetchXml=", outcome.Query.RelativeRequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count%3D", outcome.Query.RelativeRequestUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_With_Top_Preserves_Top()
    {
        var outcome = await ValidateAsync("""
            <fetch top="10">
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(10, outcome.Query!.Top);
        Assert.Contains("top=\"10\"", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count=", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_With_Count_Only_Normalizes_To_Top_Without_Count()
    {
        var outcome = await ValidateAsync("""
            <fetch version="1.0" mapping="logical" count="10">
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(10, outcome.Query!.Top);
        Assert.Contains("top=\"10\"", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count=", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("top%3D%2210%22", outcome.Query.RelativeRequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count%3D", outcome.Query.RelativeRequestUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_With_Both_Top_And_Count_Is_Rejected()
    {
        var outcome = await ValidateAsync("""
            <fetch top="10" count="10">
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_QUERY_PLAN", outcome.ErrorCode);
        Assert.Contains("both top and count", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_With_Excessive_Count_Is_Rejected()
    {
        var outcome = await ValidateAsync("""
            <fetch count="100">
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("QUERY_TOO_LARGE", outcome.ErrorCode);
        Assert.Contains(FetchXmlSafetyLimits.MaxTop.ToString(), outcome.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_With_Page_Beyond_First_And_Count_Is_Rejected_As_Unsupported_Paging()
    {
        var outcome = await ValidateAsync("""
            <fetch page="2" count="10">
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("UNSUPPORTED_FETCHXML", outcome.ErrorCode);
        Assert.Contains("Paging beyond page 1", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Aggregate_With_Count_Page_Size_Still_Accepted()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true" count="50">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.True(outcome.Query!.IsAggregate);
        Assert.Contains("count=\"50\"", outcome.Query.FetchXml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_Aggregate_Ungrouped_Root_Attribute()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="name" alias="ordername" />
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_AGGREGATE_FIELD", outcome.ErrorCode);
        Assert.Contains("salesorder.name", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groupby='true'", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_Aggregate_Ungrouped_Attribute_In_Direct_Link()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true" top="10">
              <entity name="contact">
                <attribute name="parentcustomerid" alias="accountid" groupby="true" />
                <attribute name="contactid" alias="contactcount" aggregate="count" />
                <link-entity name="account" from="accountid" to="parentcustomerid" alias="a" link-type="inner">
                  <attribute name="name" alias="accountname" />
                </link-entity>
                <order alias="contactcount" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_AGGREGATE_FIELD", outcome.ErrorCode);
        Assert.Contains("account.name", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groupby='true'", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_Aggregate_Ungrouped_Attribute_In_Nested_Link()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="salesorderid" alias="ordercount" aggregate="count" />
                <link-entity name="account" from="accountid" to="customerid" alias="a" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="accountid" alias="acct" groupby="true" />
                  <link-entity name="contact" from="contactid" to="primarycontactid" alias="pc" link-type="outer" relationship="account_primary_contact">
                    <attribute name="fullname" alias="buyer" />
                  </link-entity>
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_AGGREGATE_FIELD", outcome.ErrorCode);
        Assert.Contains("contact.fullname", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Accepts_Aggregate_Grouped_Attribute_In_Direct_Link()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true" top="10">
              <entity name="contact">
                <attribute name="parentcustomerid" alias="accountid" groupby="true" />
                <attribute name="contactid" alias="contactcount" aggregate="count" />
                <link-entity name="account" from="accountid" to="parentcustomerid" alias="a" link-type="inner">
                  <attribute name="accountid" alias="acct_id" groupby="true" />
                  <attribute name="name" alias="accountname" groupby="true" />
                </link-entity>
                <order alias="contactcount" descending="true" />
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.True(outcome.Query!.IsAggregate);
        Assert.Contains(outcome.Query.Columns, c => c.Key == "accountname");
        Assert.Contains(outcome.Query.Columns, c => c.Key == "acct_id");
    }

    [Fact]
    public async Task Accepts_Aggregate_Grouped_Attribute_In_Nested_Link()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="salesorderid" alias="ordercount" aggregate="count" />
                <link-entity name="account" from="accountid" to="customerid" alias="a" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="accountid" alias="acct" groupby="true" />
                  <link-entity name="contact" from="contactid" to="primarycontactid" alias="pc" link-type="outer" relationship="account_primary_contact">
                    <attribute name="contactid" alias="buyer_id" groupby="true" />
                    <attribute name="fullname" alias="buyer" groupby="true" />
                  </link-entity>
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Contains(outcome.Query!.Columns, c => c.Key == "buyer");
        Assert.Contains(outcome.Query.Columns, c => c.Key == "buyer_id");
    }

    [Fact]
    public async Task Rejects_Aggregate_GroupBy_PrimaryName_Without_PrimaryId()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <link-entity name="account" from="accountid" to="customerid" alias="a" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="name" alias="accountname" groupby="true" />
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("AGGREGATE_GROUP_IDENTITY", outcome.ErrorCode);
        Assert.Contains("accountid", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Accepts_Aggregate_Calculation_Inside_Link()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="customerid" alias="customer" groupby="true" />
                <link-entity name="account" from="accountid" to="customerid" alias="a" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="accountid" alias="acctcount" aggregate="count" />
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_Aggregate_Attribute_With_Both_GroupBy_And_Aggregate()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" groupby="true" aggregate="sum" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_AGGREGATE_FIELD", outcome.ErrorCode);
        Assert.Contains("salesorder.totalamount", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot specify both", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_Aggregate_Attribute_Missing_Alias()
    {
        var outcome = await ValidateAsync("""
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" aggregate="sum" />
              </entity>
            </fetch>
            """);

        Assert.False(outcome.Success);
        Assert.Equal("INVALID_ALIAS", outcome.ErrorCode);
        Assert.Contains("requires an alias", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonAggregate_Join_Projections_Remain_Valid()
    {
        var outcome = await ValidateAsync("""
            <fetch top="10">
              <entity name="salesorder">
                <attribute name="name" />
                <link-entity name="account" from="accountid" to="customerid" alias="customer" link-type="inner" relationship="salesorder_customer_accounts">
                  <attribute name="name" />
                </link-entity>
              </entity>
            </fetch>
            """);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.False(outcome.Query!.IsAggregate);
    }

    [Fact]
    public async Task Rejects_LegacyStaticEntity_When_Removed_From_ApprovedEntities()
    {
        // salesorder remains on CrmEntityAllowlist but is omitted from config → must deny.
        var outcome = await ValidateAsync("""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
              </entity>
            </fetch>
            """, approvedEntities: ["account", "contact", "lead"]);

        Assert.False(outcome.Success);
        Assert.True(
            outcome.ErrorCode is "ENTITY_NOT_APPROVED" or "AUTHORIZATION_DENIED",
            $"Unexpected code: {outcome.ErrorCode}; {outcome.ErrorMessage}");
    }

    private static async Task<FetchXmlValidationOutcome> ValidateAsync(
        string fetchXml,
        IEnumerable<string>? approvedEntities = null)
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            ApprovedEntities = (approvedEntities ?? ["salesorder", "account", "contact", "lead"]).ToList()
        });
        var policy = new EntityApprovalPolicy(crmOptions);
        var auth = new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, policy);
        var validator = new FetchXmlSafetyValidator(new FakeMetadata(), policy, auth);
        return await validator.ValidateAsync(new FetchXmlToolRequest
        {
            FetchXml = fetchXml,
            ResultMode = FetchXmlResultMode.Complete
        });
    }

    private sealed class FakeMetadata : ICrmMetadataService
    {
        private readonly ConcurrentDictionary<string, CrmEntityMetadata> _entities = new(StringComparer.OrdinalIgnoreCase);

        public FakeMetadata()
        {
            _entities["salesorder"] = CreateSalesOrder();
            _entities["account"] = CreateAccount();
            _entities["contact"] = CreateContact();
        }

        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entities.TryGetValue(logicalName, out var e) ? e : null);

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmEntityMetadata>>(_entities.Values.ToList());

        public Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

                public Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
            string entityLogicalName,
            string attributeLogicalName,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (GetEntityAsync(entityLogicalName).Result is { } entity &&
                entity.TryGetAttribute(attributeLogicalName, out var attr))
            {
                return Task.FromResult(new AttributeOptionsResult
                {
                    Attribute = attr,
                    OptionsAvailable = attr.OptionSetByValue.Count > 0 || attr.OptionSetByLabel.Count > 0,
                    AlreadyCached = true
                });
            }

            return Task.FromResult(new AttributeOptionsResult
            {
                Error = $"Attribute '{attributeLogicalName}' was not found on '{entityLogicalName}'."
            });
        }

        public string GetAiCatalogSummary() => string.Empty;

        private static CrmEntityMetadata CreateSalesOrder() =>
            new()
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("salesorderid", CrmAttributeKind.Uniqueidentifier),
                    Attr("name", CrmAttributeKind.String),
                    Attr("ordernumber", CrmAttributeKind.String),
                    Attr("createdon", CrmAttributeKind.DateTime, sortable: true),
                    Attr("totalamount", CrmAttributeKind.Money, sortable: true),
                    Attr("customerid", CrmAttributeKind.Lookup),
                    Attr("primarycontactid", CrmAttributeKind.Lookup),
                    Attr("secretnote", CrmAttributeKind.String, readable: false)
                ],
                Relationships =
                [
                    Rel("salesorder_customer_accounts", "salesorder", "account", "customerid", "accountid"),
                    Rel("salesorder_primarycontact", "salesorder", "contact", "primarycontactid", "contactid")
                ]
            };

        private static CrmEntityMetadata CreateAccount() =>
            new()
            {
                LogicalName = "account",
                EntitySetName = "accounts",
                DisplayName = "Account",
                PrimaryIdAttribute = "accountid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("accountid", CrmAttributeKind.Uniqueidentifier),
                    Attr("name", CrmAttributeKind.String),
                    Attr("telephone1", CrmAttributeKind.String),
                    Attr("emailaddress1", CrmAttributeKind.String),
                    Attr("primarycontactid", CrmAttributeKind.Lookup)
                ],
                Relationships =
                [
                    Rel("salesorder_customer_accounts", "salesorder", "account", "customerid", "accountid"),
                    Rel("account_primary_contact", "account", "contact", "primarycontactid", "contactid")
                ]
            };

        private static CrmEntityMetadata CreateContact() =>
            new()
            {
                LogicalName = "contact",
                EntitySetName = "contacts",
                DisplayName = "Contact",
                PrimaryIdAttribute = "contactid",
                PrimaryNameAttribute = "fullname",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("contactid", CrmAttributeKind.Uniqueidentifier),
                    Attr("fullname", CrmAttributeKind.String),
                    Attr("parentcustomerid", CrmAttributeKind.Lookup)
                ],
                Relationships =
                [
                    Rel("salesorder_primarycontact", "salesorder", "contact", "primarycontactid", "contactid"),
                    Rel("account_primary_contact", "account", "contact", "primarycontactid", "contactid"),
                    Rel("contact_customer_accounts", "contact", "account", "parentcustomerid", "accountid")
                ]
            };

        private static CrmAttributeMetadata Attr(
            string name,
            CrmAttributeKind kind,
            bool sortable = false,
            bool readable = true) =>
            new()
            {
                LogicalName = name,
                ODataPropertyName = name,
                Kind = kind,
                IsValidForRead = readable,
                IsFilterable = readable,
                IsSortable = sortable && readable,
                AllowedOperators = AttributeKindMapper.DefaultOperators(kind)
            };

        private static CrmRelationshipMetadata Rel(
            string schema,
            string referencing,
            string referenced,
            string referencingAttr,
            string referencedAttr) =>
            new()
            {
                SchemaName = schema,
                ReferencingEntity = referencing,
                ReferencedEntity = referenced,
                ReferencingAttribute = referencingAttr,
                ReferencedAttribute = referencedAttr,
                Kind = CrmRelationshipKind.ManyToOne,
                IsApproved = true
            };
    }
}
