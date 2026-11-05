using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Catalog vocabulary helpers for approved entity alias/metadata matching.
/// Does not rewrite AI query plans or invent business filters/operations.
/// </summary>
public static class CrmQueryIntentHelper
{
    /// <summary>
    /// Resolves an approved entity logical name from message vocabulary when uniquely determined.
    /// Used for diagnostics and catalog checks — not for fabricating query_crm tool calls.
    /// </summary>
    public static string? DetectEntityLogicalName(string message) =>
        DetectEntityLogicalName(message, new EntityApprovalPolicy(Options.Create(new CrmOptions())));

    public static string? DetectEntityLogicalName(string message, EntityApprovalPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(message) || policy is null)
        {
            return null;
        }

        var stubs = policy.ApprovedLogicalNames
            .Select(logical => GenericApprovedEntityStubFactory.Create(logical, policy))
            .ToArray();
        var enriched = stubs.Select(e => new CrmEntityMetadata
        {
            LogicalName = e.LogicalName,
            EntitySetName = e.EntitySetName,
            DisplayName = e.DisplayName ?? e.LogicalName,
            PrimaryIdAttribute = e.PrimaryIdAttribute,
            PrimaryNameAttribute = e.PrimaryNameAttribute,
            IsApprovedForAi = e.IsApprovedForAi,
            Aliases = policy.GetAliases(e.LogicalName),
            Attributes = e.Attributes,
            Relationships = e.Relationships
        }).ToArray();

        var resolved = CrmEntityResolver.Resolve(message, aiProposedEntity: null, enriched, policy);
        return resolved.Status == CrmEntityResolutionStatus.Resolved ? resolved.LogicalName : null;
    }
}
