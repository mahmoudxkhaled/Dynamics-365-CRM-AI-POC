using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Validation;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmArgumentValidatorTests
{
    private readonly CrmArgumentValidator _validator = new(
        Options.Create(new CrmOptions { DefaultPageSize = 10, MaxPageSize = 25 }));

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
