using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Validation;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmArgumentValidatorTests
{
    private readonly CrmArgumentValidator _validator = new(
        Options.Create(new CrmOptions { DefaultPageSize = 10, MaxPageSize = 25 }));

    [Fact]
    public void ValidateSearchName_RequiresValue()
    {
        var result = _validator.ValidateSearchName("  ");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateSearchName_AcceptsNormalName()
    {
        var result = _validator.ValidateSearchName(" Ahmed ");
        Assert.True(result.IsValid);
        Assert.Equal("Ahmed", result.NormalizedValue);
    }

    [Fact]
    public void ValidateSearchName_RejectsFetchXmlPayload()
    {
        var result = _validator.ValidateSearchName("<fetch><entity name='account'/></fetch>");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateAccountId_RequiresGuid()
    {
        Assert.False(_validator.ValidateAccountId("not-a-guid").IsValid);
        Assert.True(_validator.ValidateAccountId("11111111-1111-1111-1111-111111111111").IsValid);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(3, 3)]
    [InlineData(100, 25)]
    [InlineData(-5, 10)]
    public void ClampLimit_EnforcesBounds(int? input, int expected)
    {
        Assert.Equal(expected, _validator.ClampLimit(input));
    }
}
