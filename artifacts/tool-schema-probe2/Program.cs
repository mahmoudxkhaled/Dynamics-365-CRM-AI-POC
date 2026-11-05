using System.ComponentModel;
using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Crm.Generic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

var tools = new CrmAiTools(new NoopRepo(), NullLogger<CrmAiTools>.Instance);
var f = AIFunctionFactory.Create(tools.query_crm);
Console.WriteLine("Name=" + f.Name);
Console.WriteLine("Description=" + f.Description);
Console.WriteLine(JsonSerializer.Serialize(f.JsonSchema, new JsonSerializerOptions { WriteIndented = true }));

sealed class NoopRepo : IGenericCrmRepository
{
    public string ProviderName => "noop";
    public Task<GenericCrmQueryResult> QueryAsync(CrmQueryPlan plan, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
