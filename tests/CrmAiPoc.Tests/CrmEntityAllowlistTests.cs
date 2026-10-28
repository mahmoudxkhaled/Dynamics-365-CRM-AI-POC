using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Tests;

public class CrmEntityAllowlistTests
{
    [Fact]
    public void OnlyAccountAndContactAreAllowed()
    {
        Assert.True(CrmEntityAllowlist.IsEntityAllowed("account"));
        Assert.True(CrmEntityAllowlist.IsEntityAllowed("contact"));
        Assert.False(CrmEntityAllowlist.IsEntityAllowed("lead"));
        Assert.False(CrmEntityAllowlist.IsEntityAllowed("incident"));
    }

    [Fact]
    public void FieldsAreExplicitlyAllowlisted()
    {
        Assert.True(CrmEntityAllowlist.IsFieldAllowed("account", "name"));
        Assert.False(CrmEntityAllowlist.IsFieldAllowed("account", "creditlimit"));
        Assert.False(CrmEntityAllowlist.IsFieldAllowed("contact", "governmentid"));
    }
}
