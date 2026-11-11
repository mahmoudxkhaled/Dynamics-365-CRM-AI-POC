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

        // Thin vocabulary entities from approval config only — not CRM schema seeds.
        var vocabulary = policy.ApprovedLogicalNames.Select(logical =>
        {
            var name = logical.Trim().ToLowerInvariant();
            return new CrmEntityMetadata
            {
                LogicalName = name,
                EntitySetName = name + "s",
                DisplayName = char.ToUpperInvariant(name[0]) + name[1..],
                PrimaryIdAttribute = name + "id",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Aliases = policy.GetAliases(name),
                Attributes = Array.Empty<CrmAttributeMetadata>(),
                Relationships = Array.Empty<CrmRelationshipMetadata>(),
                Provenance = CrmMetadataProvenance.Live
            };
        }).ToArray();

        var resolved = CrmEntityResolver.Resolve(message, aiProposedEntity: null, vocabulary, policy);
        return resolved.Status == CrmEntityResolutionStatus.Resolved ? resolved.LogicalName : null;
    }
}
