using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmAiToolsTests
{
    private readonly CrmAiTools _tools;

    public CrmAiToolsTests()
    {
        var options = Options.Create(new CrmOptions());
        _tools = new CrmAiTools(
            new MockCrmDataProvider(NullLogger<MockCrmDataProvider>.Instance),
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance),
            new CrmArgumentValidator(options),
            options,
            NullLogger<CrmAiTools>.Instance);
    }

    [Fact]
    public async Task SearchAccounts_ReturnsAhmedMatches()
    {
        var json = await _tools.search_accounts("Ahmed", 10);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.GetProperty("totalMatchedVisible").GetInt32() >= 2);

        foreach (var account in doc.RootElement.GetProperty("accounts").EnumerateArray())
        {
            Assert.Contains("Ahmed", account.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SearchAccounts_DoesNotReturnConfidentialAccount()
    {
        var json = await _tools.search_accounts("Confidential", 10);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("totalMatchedVisible").GetInt32());
    }

    [Fact]
    public async Task SearchAccounts_DoesNotInventResults()
    {
        var json = await _tools.search_accounts("ZZZ-NO-MATCH", 10);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("totalMatchedVisible").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("accounts").GetArrayLength());
    }

    [Fact]
    public async Task GetContactsForAccount_ReturnsRelatedContacts()
    {
        var json = await _tools.get_contacts_for_account(MockCrmSeedData.AhmedTradingId.ToString(), 10);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("totalMatched").GetInt32() >= 1);
    }

    [Fact]
    public async Task GetContactsForAccount_DeniesConfidentialAccount()
    {
        var json = await _tools.get_contacts_for_account(
            CrmAuthorizationService.DeniedConfidentialAccountId.ToString(), 10);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
        Assert.Contains("permission", doc.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetContactsForAccount_UsesConversationContext()
    {
        var session = new ConversationSession("test");
        session.LastReferencedAccountId = MockCrmSeedData.AhmedTechId;
        _tools.Session = session;

        var json = await _tools.get_contacts_for_account(accountId: null, limit: 10);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(MockCrmSeedData.AhmedTechId, doc.RootElement.GetProperty("accountId").GetGuid());
    }

    [Fact]
    public async Task CountAccounts_ReturnsPositiveCountExcludingConfidential()
    {
        var json = await _tools.count_accounts();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SearchAccounts_EnforcesMaxLimit()
    {
        var json = await _tools.search_accounts("a", limit: 1000);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(25, doc.RootElement.GetProperty("limit").GetInt32());
    }
}
