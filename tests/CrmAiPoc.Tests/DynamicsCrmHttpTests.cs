using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;

namespace CrmAiPoc.Tests;

public class DynamicsCrmHttpTests
{
    [Fact]
    public void CreateHandler_UsesDefaultCredentials_WhenConfigured()
    {
        using var handler = (HttpClientHandler)DynamicsCrmHttp.CreateHandler(new CrmOptions
        {
            UseDefaultCredentials = true
        });

        Assert.True(handler.UseDefaultCredentials);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void CreateHandler_UsesExplicitCredentials_WhenProvided()
    {
        using var handler = (HttpClientHandler)DynamicsCrmHttp.CreateHandler(new CrmOptions
        {
            UseDefaultCredentials = true,
            Username = "crmuser",
            Password = "not-logged",
            Domain = "TE"
        });

        Assert.False(handler.UseDefaultCredentials);
        Assert.NotNull(handler.Credentials);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void CrmOptions_IsDynamicsProvider_AcceptsAliases()
    {
        Assert.True(new CrmOptions { Provider = "Dynamics365" }.IsDynamicsProvider);
        Assert.True(new CrmOptions { Provider = "DynamicsOnPrem" }.IsDynamicsProvider);
        Assert.False(new CrmOptions { Provider = "Mock" }.IsDynamicsProvider);
    }
}
