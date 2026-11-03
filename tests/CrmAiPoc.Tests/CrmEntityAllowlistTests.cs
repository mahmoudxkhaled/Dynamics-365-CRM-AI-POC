using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Tests;

public class CrmEntityAllowlistTests
{
    [Fact]
    public void OnlyCoreEntitiesAreAllowed()
    {
        Assert.True(CrmEntityAllowlist.IsEntityAllowed("account"));
        Assert.True(CrmEntityAllowlist.IsEntityAllowed("contact"));
        Assert.True(CrmEntityAllowlist.IsEntityAllowed("salesorder"));
        Assert.False(CrmEntityAllowlist.IsEntityAllowed("opportunity"));
        Assert.False(CrmEntityAllowlist.IsEntityAllowed("lead"));
        Assert.False(CrmEntityAllowlist.IsEntityAllowed("incident"));
    }
}
