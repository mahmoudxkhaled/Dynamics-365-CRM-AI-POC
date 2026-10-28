using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Endpoints;

public static class CrmDiagnosticsEndpoints
{
    public static RouteGroupBuilder MapCrmDiagnosticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/crm").WithTags("CRM Diagnostics");

        group.MapGet("/diagnostics/whoami", async (
            DynamicsConnectivityService connectivity,
            CancellationToken cancellationToken) =>
        {
            var result = await connectivity.WhoAmIAsync(cancellationToken);
            return result.Success ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("CrmWhoAmI");

        group.MapGet("/diagnostics/identity", async (
            HttpContext httpContext,
            DynamicsConnectivityService connectivity,
            CancellationToken cancellationToken) =>
        {
            var result = await connectivity.AuditIdentityAsync(httpContext.User, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("CrmIdentityAudit");

        group.MapGet("/diagnostics/salesorder-entity", async (
            DynamicsConnectivityService connectivity,
            CancellationToken cancellationToken) =>
        {
            var result = await connectivity.GetSalesOrderEntityDefinitionAsync(cancellationToken);
            return result.Success ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("CrmSalesOrderEntityDefinition");

        group.MapGet("/diagnostics/salesorder-schema", async (
            DynamicsSchemaDiscoveryService discovery,
            IHostEnvironment env,
            CancellationToken cancellationToken) =>
        {
            var result = await discovery.DiscoverSalesOrderSchemaAsync(cancellationToken);
            if (!result.Success)
            {
                return Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!string.IsNullOrWhiteSpace(result.MarkdownReport))
            {
                var docsDir = Path.Combine(env.ContentRootPath, "..", "..", "docs");
                Directory.CreateDirectory(docsDir);
                var reportPath = Path.GetFullPath(Path.Combine(docsDir, "CRM-SALESORDER-SCHEMA.md"));
                await File.WriteAllTextAsync(reportPath, result.MarkdownReport, cancellationToken);
            }

            // Do not return full attribute dump with potentially sensitive labels to clients by default.
            return Results.Ok(new
            {
                result.Success,
                result.LogicalName,
                result.EntitySetName,
                result.PrimaryIdAttribute,
                result.PrimaryNameAttribute,
                recommendedSelectFields = result.RecommendedSelectFields,
                customAttributeCount = result.CustomAttributes.Count,
                lookupAttributeCount = result.LookupAttributes.Count,
                readableAttributeCount = result.Attributes.Count,
                customAttributeNames = result.CustomAttributes.Select(a => a.LogicalName).Take(50),
                lookupAttributeNames = result.LookupAttributes.Select(a => a.LogicalName).Take(50),
                reportWritten = "docs/CRM-SALESORDER-SCHEMA.md",
                result.Message
            });
        })
        .WithName("CrmSalesOrderSchemaDiscovery");

        group.MapGet("/salesorders", async (
            int? limit,
            IGenericCrmRepository repository,
            ICrmAuthorizationService auth,
            CrmArgumentValidator validator,
            ICrmMetadataService metadata,
            CancellationToken cancellationToken) =>
        {
            var decision = auth.AuthorizeRead(CrmEntityAllowlist.SalesOrder, "query_crm");
            if (!decision.Allowed)
            {
                return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
            }

            await metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
            var safeLimit = validator.ClampLimit(limit);
            var result = await repository.QueryAsync(new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Sort =
                [
                    new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }
                ],
                Limit = safeLimit
            }, cancellationToken);

            return Results.Ok(new
            {
                provider = result.ProviderName,
                limit = safeLimit,
                totalReturned = result.Items.Count,
                relativeRequestUri = result.RelativeRequestUri,
                note = result.Message,
                salesOrders = result.Items
            });
        })
        .WithName("CrmGetSalesOrders");

        group.MapGet("/diagnostics/entities", async (
            ICrmMetadataService metadata,
            EntityApprovalPolicy policy,
            CancellationToken cancellationToken) =>
        {
            await metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
            var approved = await metadata.GetApprovedEntitiesAsync(cancellationToken);
            return Results.Ok(new
            {
                approvedLogicalNames = policy.ApprovedLogicalNames,
                entities = approved.Select(e => new
                {
                    e.LogicalName,
                    e.EntitySetName,
                    e.DisplayName,
                    e.PrimaryIdAttribute,
                    e.PrimaryNameAttribute,
                    e.Aliases,
                    attributeCount = e.Attributes.Count,
                    relationshipCount = e.Relationships.Count,
                    e.DiscoveredAtUtc
                }),
                aiCatalogSummary = metadata.GetAiCatalogSummary()
            });
        })
        .WithName("CrmApprovedEntities");

        group.MapGet("/diagnostics/entities/{logicalName}", async (
            string logicalName,
            ICrmMetadataService metadata,
            EntityApprovalPolicy policy,
            CancellationToken cancellationToken) =>
        {
            if (!policy.IsApproved(logicalName) && !policy.TryResolveEntityAlias(logicalName, out logicalName))
            {
                return Results.Json(new { error = "Entity is not approved for AI/diagnostics inspection." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var entity = await metadata.GetEntityAsync(logicalName, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { error = "Entity metadata not found." });
            }

            return Results.Ok(new
            {
                entity.LogicalName,
                entity.EntitySetName,
                entity.DisplayName,
                entity.PrimaryIdAttribute,
                entity.PrimaryNameAttribute,
                entity.Aliases,
                fields = entity.Attributes.Select(a => new
                {
                    a.LogicalName,
                    a.ODataPropertyName,
                    a.DisplayName,
                    kind = a.Kind.ToString(),
                    a.IsFilterable,
                    a.IsSortable,
                    a.IsSearchable,
                    operators = a.AllowedOperators.Select(o => o.ToString()),
                    optionCount = a.OptionSetByValue.Count
                }),
                relationships = entity.Relationships.Where(r => r.IsApproved).Select(r => new
                {
                    r.SchemaName,
                    kind = r.Kind.ToString(),
                    r.ReferencingEntity,
                    r.ReferencedEntity,
                    r.ReferencingAttribute,
                    r.ReferencedAttribute,
                    r.ReferencingEntityNavigationPropertyName,
                    r.ReferencedEntityNavigationPropertyName
                }),
                supportedOperations = new[] { "list", "count" }
            });
        })
        .WithName("CrmEntityMetadata");

        group.MapPost("/diagnostics/metadata/refresh", async (
            string? logicalName,
            ICrmMetadataService metadata,
            CancellationToken cancellationToken) =>
        {
            await metadata.RefreshAsync(logicalName, cancellationToken);
            return Results.Ok(new
            {
                refreshed = logicalName ?? "(all approved)",
                aiCatalogSummary = metadata.GetAiCatalogSummary()
            });
        })
        .WithName("CrmMetadataRefresh");

        group.MapPost("/diagnostics/query", async (
            CrmQueryPlan plan,
            IGenericCrmRepository repository,
            EntityApprovalPolicy policy,
            CancellationToken cancellationToken) =>
        {
            if (!policy.IsApproved(plan.Entity) && !policy.TryResolveEntityAlias(plan.Entity, out _))
            {
                return Results.Json(new { error = "Entity is not approved." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await repository.QueryAsync(plan, cancellationToken);
            return Results.Ok(new
            {
                result.Entity,
                operation = result.Operation.ToString(),
                result.RelativeRequestUri,
                result.AppliedFilterSummary,
                result.SortSummary,
                result.Limit,
                result.Count,
                result.CountMayBeCapped,
                totalReturned = result.Items.Count,
                result.Message,
                result.ProviderName
                // Intentionally omit raw row payloads from diagnostics summary by default.
            });
        })
        .WithName("CrmGenericQueryDryRun");

        return group;
    }
}
