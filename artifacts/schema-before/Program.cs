using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var options = Options.Create(new CrmOptions
{
    ApprovedEntities = ["account"],
    BusinessTimeZoneId = "Africa/Cairo"
});
var policy = new EntityApprovalPolicy(options);
var registry = new CrmMetadataRegistry();
var metadata = new MockCrmMetadataService(registry, policy);
var builder = new GenericCrmQueryBuilder(options);
var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
var repo = new MockGenericCrmRepository(metadata, policy, builder, new FetchXmlAggregateBuilder(), auth);
var tools = new CrmAiTools(repo, NullLogger<CrmAiTools>.Instance);
var fn = AIFunctionFactory.Create(tools.query_crm);
var schema = fn.JsonSchema;
Console.WriteLine(schema.GetRawText());
if (schema.TryGetProperty("properties", out var props) && props.TryGetProperty("limit", out var limit))
{
    Console.WriteLine("---LIMIT---");
    Console.WriteLine(limit.GetRawText());
}
