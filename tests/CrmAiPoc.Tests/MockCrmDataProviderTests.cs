using CrmAiPoc.Api.Crm.Mock;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrmAiPoc.Tests;

public class MockCrmDataProviderTests
{
    private readonly MockCrmDataProvider _provider = new(NullLogger<MockCrmDataProvider>.Instance);

    [Fact]
    public async Task SearchIsCaseInsensitive()
    {
        var result = await _provider.SearchAccountsAsync("ahmed", 10);
        Assert.True(result.TotalMatched >= 2);
    }

    [Fact]
    public async Task ContactsAreRelatedToAccounts()
    {
        var contacts = await _provider.GetContactsForAccountAsync(MockCrmSeedData.NileRetailId, 10);
        Assert.All(contacts.Items, c => Assert.Equal(MockCrmSeedData.NileRetailId, c.AccountId));
    }

    [Fact]
    public void ProviderNameIsMock()
    {
        Assert.Equal("Mock", _provider.ProviderName);
    }
}
