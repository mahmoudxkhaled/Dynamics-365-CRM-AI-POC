using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Validation;

public sealed class CrmArgumentValidator
{
    private readonly CrmOptions _options;

    public CrmArgumentValidator(IOptions<CrmOptions> options)
    {
        _options = options.Value;
    }

    public int ClampLimit(int? limit)
    {
        var value = limit is null or <= 0 ? _options.DefaultPageSize : limit.Value;
        return Math.Clamp(value, 1, _options.MaxPageSize);
    }
}
