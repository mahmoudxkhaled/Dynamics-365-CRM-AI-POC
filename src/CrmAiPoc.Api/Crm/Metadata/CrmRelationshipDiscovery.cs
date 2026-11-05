using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Discovers approved join paths between approved entities from CRM metadata.
/// Does not invent SchemaNames — only returns relationships already present and approved.
/// </summary>
public static class CrmRelationshipDiscovery
{
    public sealed class PathResult
    {
        public bool Success { get; init; }
        public bool Ambiguous { get; init; }
        public string? Error { get; init; }
        public CrmRelationshipMetadata? Relationship { get; init; }
        public IReadOnlyList<CrmRelationshipMetadata> Candidates { get; init; } =
            Array.Empty<CrmRelationshipMetadata>();

        public static PathResult Ok(CrmRelationshipMetadata rel) =>
            new() { Success = true, Relationship = rel, Candidates = [rel] };

        public static PathResult AmbiguousPaths(IReadOnlyList<CrmRelationshipMetadata> candidates) =>
            new()
            {
                Ambiguous = true,
                Candidates = candidates,
                Error =
                    "Multiple approved relationships exist between these entities. " +
                    "Choose a business relationship option."
            };

        public static PathResult Fail(string error) =>
            new() { Error = error };
    }

    public sealed class AutoAttachResult
    {
        public required CrmQueryPlan Plan { get; init; }
        public string? ClarificationMessage { get; init; }
        public PendingRelationshipClarification? Pending { get; init; }
    }

    public static PathResult FindUniquePath(
        CrmEntityMetadata fromEntity,
        CrmEntityMetadata toEntity,
        string? userMessage = null,
        CrmRelationshipLabeler? labeler = null)
    {
        var candidates = EnumerateApprovedPaths(fromEntity, toEntity)
            .GroupBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (candidates.Count == 0)
        {
            return PathResult.Fail(
                $"No approved relationship between '{fromEntity.LogicalName}' and '{toEntity.LogicalName}'.");
        }

        if (candidates.Count > 1)
        {
            var narrowed = PreferByBusinessContext(candidates, userMessage, fromEntity, labeler);
            if (narrowed.Count == 1)
            {
                return PathResult.Ok(narrowed[0]);
            }

            return PathResult.AmbiguousPaths(narrowed.Count > 0 ? narrowed : candidates);
        }

        return PathResult.Ok(candidates[0]);
    }

    /// <summary>
    /// Resolves technical SchemaNames on AI-proposed links using CRM metadata.
    /// Does not invent links from user keywords — the AI must request related entities via linksJson.
    /// </summary>
    public static AutoAttachResult ResolveProposedLinks(
        CrmQueryPlan plan,
        string? userMessage,
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        EntityApprovalPolicy policy,
        CrmRelationshipLabeler labeler,
        string languageHint = "en")
    {
        _ = policy;
        if (plan.Links.Count == 0 || plan.Operation == CrmQueryOperation.Aggregate)
        {
            return new AutoAttachResult { Plan = plan };
        }

        var root = approvedEntities.FirstOrDefault(e =>
            e.LogicalName.Equals(plan.Entity, StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            return new AutoAttachResult { Plan = plan };
        }

        var resolvedLinks = new List<CrmLinkSpec>();
        foreach (var link in plan.Links)
        {
            var resolved = ResolveOneLink(
                plan, root, link, userMessage, approvedEntities, labeler, languageHint);
            if (resolved.Pending is not null || !string.IsNullOrWhiteSpace(resolved.ClarificationMessage))
            {
                return resolved;
            }

            resolvedLinks.AddRange(resolved.Plan.Links);
        }

        return new AutoAttachResult { Plan = CloneWithLinks(plan, resolvedLinks) };
    }

    /// <summary>
    /// Legacy name retained for tests; delegates to <see cref="ResolveProposedLinks"/>.
    /// Keyword-based auto-join from bare entity mentions is intentionally removed.
    /// </summary>
    public static AutoAttachResult TryAutoAttachRelatedLink(
        CrmQueryPlan plan,
        string? userMessage,
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        EntityApprovalPolicy policy,
        CrmRelationshipLabeler labeler,
        string languageHint = "en") =>
        ResolveProposedLinks(plan, userMessage, approvedEntities, policy, labeler, languageHint);

    private static AutoAttachResult ResolveOneLink(
        CrmQueryPlan plan,
        CrmEntityMetadata fromEntity,
        CrmLinkSpec link,
        string? userMessage,
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        CrmRelationshipLabeler labeler,
        string languageHint)
    {
        var related = approvedEntities.FirstOrDefault(e =>
            e.LogicalName.Equals(link.Entity, StringComparison.OrdinalIgnoreCase));
        if (related is null)
        {
            return new AutoAttachResult
            {
                Plan = plan,
                ClarificationMessage =
                    $"Related entity '{link.Entity}' is not an approved AI CRM entity."
            };
        }

        var resolve = ResolveSchemaName(
            fromEntity, related, link, userMessage, labeler, languageHint);
        if (resolve.Pending is not null || !string.IsNullOrWhiteSpace(resolve.ClarificationMessage))
        {
            return new AutoAttachResult
            {
                Plan = plan,
                ClarificationMessage = resolve.ClarificationMessage,
                Pending = resolve.Pending
            };
        }

        var schema = resolve.SchemaName!;

        var nested = new List<CrmLinkSpec>();
        if (link.Links.Count > 0)
        {
            foreach (var child in link.Links)
            {
                var childResult = ResolveOneLink(
                    plan, related, child, userMessage, approvedEntities, labeler, languageHint);
                if (childResult.Pending is not null ||
                    !string.IsNullOrWhiteSpace(childResult.ClarificationMessage))
                {
                    return childResult;
                }

                nested.AddRange(childResult.Plan.Links);
            }
        }

        var select = link.Select.Count > 0
            ? link.Select
            : string.IsNullOrWhiteSpace(related.PrimaryNameAttribute)
                ? Array.Empty<string>()
                : new[] { related.PrimaryNameAttribute };

        var resolvedLink = new CrmLinkSpec
        {
            Entity = related.LogicalName,
            Relationship = schema,
            RelationshipPurpose = link.RelationshipPurpose,
            Alias = link.Alias,
            JoinType = link.JoinType,
            Select = select,
            Filters = link.Filters,
            Links = nested
        };

        return new AutoAttachResult
        {
            Plan = CloneWithLinks(plan, [resolvedLink])
        };
    }

    /// <summary>
    /// Resolves a verified SchemaName from an exact catalog SchemaName, a business purpose/label,
    /// or unique metadata path scoring. Invented SchemaNames are never accepted.
    /// </summary>
    private static SchemaResolveResult ResolveSchemaName(
        CrmEntityMetadata fromEntity,
        CrmEntityMetadata related,
        CrmLinkSpec link,
        string? userMessage,
        CrmRelationshipLabeler labeler,
        string languageHint)
    {
        var candidates = CollectCandidates(fromEntity, related);
        if (candidates.Count == 0)
        {
            return new SchemaResolveResult
            {
                ClarificationMessage =
                    $"No approved relationship between {Friendly(fromEntity.LogicalName)} and {Friendly(related.LogicalName)}."
            };
        }

        // 1) Exact SchemaName from catalog (never invent).
        if (!string.IsNullOrWhiteSpace(link.Relationship))
        {
            var key = link.Relationship.Trim();
            var exact = candidates
                .Where(c => c.SchemaName.Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
            {
                return new SchemaResolveResult { SchemaName = exact[0].SchemaName };
            }

            // Treat non-catalog "relationship" values as business purpose/label hints.
            var purposeHint = string.IsNullOrWhiteSpace(link.RelationshipPurpose)
                ? key
                : $"{link.RelationshipPurpose} {key}";
            var byPurpose = PreferByBusinessContext(
                candidates,
                CombineContext(userMessage, purposeHint),
                fromEntity,
                labeler);
            if (byPurpose.Count == 1)
            {
                return new SchemaResolveResult { SchemaName = byPurpose[0].SchemaName };
            }

            if (byPurpose.Count > 1)
            {
                var ambiguous = BuildAmbiguousClarification(
                    new CrmQueryPlan { Entity = fromEntity.LogicalName, Links = [link] },
                    fromEntity,
                    related,
                    byPurpose,
                    CombineContext(userMessage, purposeHint),
                    labeler,
                    languageHint);
                return new SchemaResolveResult
                {
                    ClarificationMessage = ambiguous.ClarificationMessage,
                    Pending = ambiguous.Pending
                };
            }

            // Fall through to normal unique-path resolution using purpose + user text.
            userMessage = CombineContext(userMessage, purposeHint);
        }
        else if (!string.IsNullOrWhiteSpace(link.RelationshipPurpose))
        {
            var byPurpose = PreferByBusinessContext(
                candidates,
                CombineContext(userMessage, link.RelationshipPurpose),
                fromEntity,
                labeler);
            if (byPurpose.Count == 1)
            {
                return new SchemaResolveResult { SchemaName = byPurpose[0].SchemaName };
            }

            if (byPurpose.Count > 1)
            {
                var ambiguous = BuildAmbiguousClarification(
                    new CrmQueryPlan { Entity = fromEntity.LogicalName, Links = [link] },
                    fromEntity,
                    related,
                    byPurpose,
                    CombineContext(userMessage, link.RelationshipPurpose),
                    labeler,
                    languageHint);
                return new SchemaResolveResult
                {
                    ClarificationMessage = ambiguous.ClarificationMessage,
                    Pending = ambiguous.Pending
                };
            }

            userMessage = CombineContext(userMessage, link.RelationshipPurpose);
        }

        var path = FindUniquePath(fromEntity, related, userMessage, labeler);
        if (path.Ambiguous)
        {
            var ambiguous = BuildAmbiguousClarification(
                new CrmQueryPlan { Entity = fromEntity.LogicalName, Links = [link] },
                fromEntity,
                related,
                path.Candidates,
                userMessage,
                labeler,
                languageHint);
            return new SchemaResolveResult
            {
                ClarificationMessage = ambiguous.ClarificationMessage,
                Pending = ambiguous.Pending
            };
        }

        if (!path.Success || path.Relationship is null)
        {
            return new SchemaResolveResult
            {
                ClarificationMessage =
                    $"No approved relationship between {Friendly(fromEntity.LogicalName)} and {Friendly(related.LogicalName)}."
            };
        }

        return new SchemaResolveResult { SchemaName = path.Relationship.SchemaName };
    }

    private static List<CrmRelationshipMetadata> CollectCandidates(
        CrmEntityMetadata fromEntity,
        CrmEntityMetadata related)
    {
        return fromEntity.Relationships
            .Concat(related.Relationships)
            .Where(r => r.IsApproved)
            .Where(r =>
                (r.ReferencingEntity.Equals(fromEntity.LogicalName, StringComparison.OrdinalIgnoreCase) &&
                 r.ReferencedEntity.Equals(related.LogicalName, StringComparison.OrdinalIgnoreCase)) ||
                (r.ReferencingEntity.Equals(related.LogicalName, StringComparison.OrdinalIgnoreCase) &&
                 r.ReferencedEntity.Equals(fromEntity.LogicalName, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string? CombineContext(string? userMessage, string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return userMessage;
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return purpose.Trim();
        }

        return $"{userMessage.Trim()} {purpose.Trim()}";
    }

    private sealed class SchemaResolveResult
    {
        public string? SchemaName { get; init; }
        public string? ClarificationMessage { get; init; }
        public PendingRelationshipClarification? Pending { get; init; }
    }

    private static AutoAttachResult BuildAmbiguousClarification(
        CrmQueryPlan plan,
        CrmEntityMetadata root,
        CrmEntityMetadata related,
        IReadOnlyList<CrmRelationshipMetadata> candidates,
        string? userMessage,
        CrmRelationshipLabeler labeler,
        string languageHint)
    {
        var options = candidates
            .Select((c, i) => new RelationshipClarificationOption
            {
                Index = i + 1,
                SchemaName = c.SchemaName,
                Description = labeler.Describe(c, root)
            })
            .ToArray();

        _ = languageHint;
        var lines = options.Select(o => $"{o.Index}) {o.Description}");
        var message =
            $"Which {Friendly(related.LogicalName)} details should I include for these {Friendly(root.LogicalName)} records? Reply with the option number:\n" +
            string.Join("\n", lines);

        return new AutoAttachResult
        {
            Plan = plan,
            ClarificationMessage = message,
            Pending = new PendingRelationshipClarification
            {
                Plan = plan,
                RootEntity = root.LogicalName,
                RelatedEntity = related.LogicalName,
                OriginalUserMessage = userMessage ?? string.Empty,
                Options = options
            }
        };
    }

    /// <summary>
    /// Scores approved relationship candidates using the user's business wording plus
    /// metadata labels / lookup display names. Never invents SchemaNames.
    /// Auto-selects when one interpretation is clearly supported; otherwise returns ties for clarification.
    /// </summary>
    private static List<CrmRelationshipMetadata> PreferByBusinessContext(
        IReadOnlyList<CrmRelationshipMetadata> candidates,
        string? userMessage,
        CrmEntityMetadata fromEntity,
        CrmRelationshipLabeler? labeler)
    {
        if (candidates.Count <= 1)
        {
            return candidates.ToList();
        }

        var norm = CrmEntityResolver.Normalize(userMessage);
        var intent = ClassifyAccountContactIntent(norm, userMessage);
        var scored = new List<(CrmRelationshipMetadata Rel, int Score)>();

        foreach (var c in candidates)
        {
            scored.Add((c, ScoreCandidate(c, fromEntity, labeler, norm, intent)));
        }

        var best = scored.Max(s => s.Score);
        var leaders = scored.Where(s => s.Score == best).Select(s => s.Rel).ToList();

        // Unique positive winner → auto-select.
        if (leaders.Count == 1 && best > 0)
        {
            return leaders;
        }

        // Tied or non-positive: only clarify when multiple remain; never invent a join.
        if (leaders.Count > 1 && best > 0)
        {
            return leaders;
        }

        // No phrase match — apply trusted business defaults from metadata labels only.
        var defaults = ApplyTrustedBusinessDefaults(candidates, fromEntity, labeler, intent);
        if (defaults.Count == 1)
        {
            return defaults;
        }

        if (defaults.Count > 1)
        {
            return defaults;
        }

        return candidates.ToList();
    }

    private static int ScoreCandidate(
        CrmRelationshipMetadata c,
        CrmEntityMetadata fromEntity,
        CrmRelationshipLabeler? labeler,
        string norm,
        AccountContactIntent intent)
    {
        var score = 0;
        var label = labeler?.Describe(c, fromEntity) ?? c.BusinessLabel ?? string.Empty;
        var labelNorm = CrmEntityResolver.Normalize(label);
        var displayNorm = CrmEntityResolver.Normalize(c.ReferencingAttributeDisplayName);
        var businessNorm = CrmEntityResolver.Normalize(c.BusinessLabel);
        var attrNorm = CrmEntityResolver.Normalize(
            c.ReferencingAttribute?.Replace("_", " ", StringComparison.Ordinal));
        var role = ClassifyRelationshipRole(c, labelNorm, displayNorm, businessNorm, attrNorm);

        if (!string.IsNullOrWhiteSpace(labelNorm) &&
            !string.IsNullOrWhiteSpace(norm) &&
            norm.Contains(labelNorm, StringComparison.Ordinal))
        {
            score += 5;
        }

        if (!string.IsNullOrWhiteSpace(businessNorm) &&
            !string.IsNullOrWhiteSpace(norm) &&
            norm.Contains(businessNorm, StringComparison.Ordinal))
        {
            score += 5;
        }

        if (!string.IsNullOrWhiteSpace(displayNorm) &&
            !string.IsNullOrWhiteSpace(norm) &&
            norm.Contains(displayNorm, StringComparison.Ordinal))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(attrNorm) &&
            !string.IsNullOrWhiteSpace(norm) &&
            norm.Contains(attrNorm, StringComparison.Ordinal))
        {
            score += 2;
        }

        // Phrase fragments that map to business roles (not SchemaNames).
        if (intent.WantsCustomerAccount && role == RelationshipBusinessRole.CustomerAccount)
        {
            score += 10;
        }

        if (intent.WantsPotentialCustomer && role == RelationshipBusinessRole.PotentialCustomer)
        {
            score += 10;
        }

        if (intent.WantsBranchAccount && role == RelationshipBusinessRole.BranchAccount)
        {
            score += 10;
        }

        if (intent.WantsPrimaryContact && role == RelationshipBusinessRole.PrimaryContact)
        {
            score += 10;
        }

        if (intent.WantsPrimaryContact && role == RelationshipBusinessRole.AccountContactsCollection)
        {
            score -= 10;
        }

        if (!intent.WantsPrimaryContact && role == RelationshipBusinessRole.PrimaryContact)
        {
            score -= 5;
        }

        if (!intent.WantsPrimaryContact && role == RelationshipBusinessRole.AccountContactsCollection)
        {
            score += 3;
        }

        // Generic "account / account details" on a Sales Order: prefer Customer Account when labeled as such.
        if (intent.WantsGenericAccountDetails && role == RelationshipBusinessRole.CustomerAccount)
        {
            score += 8;
        }

        if (intent.WantsGenericAccountDetails && role == RelationshipBusinessRole.PotentialCustomer)
        {
            score += 2; // weaker default than Customer Account
        }

        if (intent.WantsGenericAccountDetails &&
            role is RelationshipBusinessRole.BranchAccount or RelationshipBusinessRole.SecondaryAccount)
        {
            score -= 3;
        }

        return score;
    }

    private static List<CrmRelationshipMetadata> ApplyTrustedBusinessDefaults(
        IReadOnlyList<CrmRelationshipMetadata> candidates,
        CrmEntityMetadata fromEntity,
        CrmRelationshipLabeler? labeler,
        AccountContactIntent intent)
    {
        if (!intent.WantsGenericAccountDetails && !intent.WantsPrimaryContact)
        {
            return [];
        }

        var roleMap = candidates
            .Select(c =>
            {
                var label = labeler?.Describe(c, fromEntity) ?? c.BusinessLabel ?? string.Empty;
                var role = ClassifyRelationshipRole(
                    c,
                    CrmEntityResolver.Normalize(label),
                    CrmEntityResolver.Normalize(c.ReferencingAttributeDisplayName),
                    CrmEntityResolver.Normalize(c.BusinessLabel),
                    CrmEntityResolver.Normalize(c.ReferencingAttribute?.Replace("_", " ", StringComparison.Ordinal)));
                return (Rel: c, Role: role);
            })
            .ToList();

        if (intent.WantsPrimaryContact)
        {
            var primary = roleMap
                .Where(x => x.Role == RelationshipBusinessRole.PrimaryContact)
                .Select(x => x.Rel)
                .ToList();
            return primary.Count == 1 ? primary : [];
        }

        // Generic account details: unique Customer Account label/display wins.
        var customerAccounts = roleMap
            .Where(x => x.Role == RelationshipBusinessRole.CustomerAccount)
            .Select(x => x.Rel)
            .ToList();
        if (customerAccounts.Count == 1)
        {
            return customerAccounts;
        }

        // If metadata has no Customer Account role, a single Potential Customer is acceptable.
        if (customerAccounts.Count == 0)
        {
            var potential = roleMap
                .Where(x => x.Role == RelationshipBusinessRole.PotentialCustomer)
                .Select(x => x.Rel)
                .ToList();
            if (potential.Count == 1)
            {
                return potential;
            }
        }

        return [];
    }

    private static AccountContactIntent ClassifyAccountContactIntent(string norm, string? raw)
    {
        var wantsPrimary = LooksLikePrimaryContactRequest(raw);
        var wantsPotential =
            norm.Contains("potential customer", StringComparison.Ordinal) ||
            norm.Contains("potentialcustomer", StringComparison.Ordinal);
        // "potential customer account" contains the substring "customer account" — exclude that case.
        var wantsCustomerAccount = !wantsPotential &&
            (norm.Contains("customer account", StringComparison.Ordinal) ||
             norm.Contains("customeraccount", StringComparison.Ordinal));
        var wantsBranch =
            norm.Contains("branch account", StringComparison.Ordinal) ||
            (norm.Contains("branch", StringComparison.Ordinal) &&
             norm.Contains("account", StringComparison.Ordinal));

        // Generic account / account details / account name — without a more specific role phrase.
        var mentionsAccount =
            norm.Contains("account details", StringComparison.Ordinal) ||
            norm.Contains("account name", StringComparison.Ordinal) ||
            norm.Contains("account names", StringComparison.Ordinal) ||
            ContainsWholePhrase(norm, "account") ||
            ContainsWholePhrase(norm, "accounts");

        var wantsGeneric = mentionsAccount &&
                           !wantsCustomerAccount &&
                           !wantsPotential &&
                           !wantsBranch &&
                           !wantsPrimary;

        return new AccountContactIntent(
            wantsCustomerAccount,
            wantsPotential,
            wantsBranch,
            wantsPrimary,
            wantsGeneric);
    }

    private static bool ContainsWholePhrase(string norm, string phrase)
    {
        if (string.IsNullOrWhiteSpace(norm) || string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var padded = $" {norm} ";
        return padded.Contains($" {phrase} ", StringComparison.Ordinal);
    }

    private static RelationshipBusinessRole ClassifyRelationshipRole(
        CrmRelationshipMetadata rel,
        string labelNorm,
        string displayNorm,
        string businessNorm,
        string attrNorm)
    {
        var blob = $"{labelNorm} {displayNorm} {businessNorm} {attrNorm}";

        if (IsPrimaryContactRelationship(rel) ||
            blob.Contains("primary contact", StringComparison.Ordinal) ||
            attrNorm.Contains("primarycontact", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.PrimaryContact;
        }

        if (blob.Contains("account contact", StringComparison.Ordinal) ||
            attrNorm.Contains("parentcustomer", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.AccountContactsCollection;
        }

        // Check Potential Customer before Customer Account — labels like
        // "Potential Customer (Account)" contain the substring "customer account".
        if (blob.Contains("potential customer", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.PotentialCustomer;
        }

        if (blob.Contains("customer account", StringComparison.Ordinal) ||
            attrNorm.Contains("customeraccount", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.CustomerAccount;
        }

        if (blob.Contains("branch", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.BranchAccount;
        }

        if (blob.Contains("calculated", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.SecondaryAccount;
        }

        // OOB customerid without "potential" wording still counts as a weaker customer role.
        if (attrNorm.Equals("customerid", StringComparison.Ordinal) ||
            blob.Contains("customer", StringComparison.Ordinal))
        {
            return RelationshipBusinessRole.PotentialCustomer;
        }

        return RelationshipBusinessRole.Other;
    }

    private enum RelationshipBusinessRole
    {
        Other = 0,
        CustomerAccount = 1,
        PotentialCustomer = 2,
        BranchAccount = 3,
        SecondaryAccount = 4,
        PrimaryContact = 5,
        AccountContactsCollection = 6
    }

    private readonly record struct AccountContactIntent(
        bool WantsCustomerAccount,
        bool WantsPotentialCustomer,
        bool WantsBranchAccount,
        bool WantsPrimaryContact,
        bool WantsGenericAccountDetails);

    private static List<CrmRelationshipMetadata> PreferPrimaryContact(
        IReadOnlyList<CrmRelationshipMetadata> candidates) =>
        candidates.Where(IsPrimaryContactRelationship).ToList();

    private static bool IsPrimaryContactRelationship(CrmRelationshipMetadata rel) =>
        rel.SchemaName.Equals("account_primary_contact", StringComparison.OrdinalIgnoreCase) ||
        (rel.ReferencingAttribute?.Equals("primarycontactid", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (rel.BusinessLabel?.Equals("Primary Contact", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool LooksLikePrimaryContactRequest(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return false;
        }

        var norm = CrmEntityResolver.Normalize(userMessage);
        return norm.Contains("primary contact", StringComparison.Ordinal) ||
               norm.Contains("primarycontact", StringComparison.Ordinal) ||
               userMessage.Contains("Primary Contact", StringComparison.OrdinalIgnoreCase);
    }

    public static CrmQueryPlan AttachSelectedRelationship(
        PendingRelationshipClarification pending,
        string schemaName,
        IReadOnlyList<CrmEntityMetadata> approvedEntities)
    {
        var related = approvedEntities.FirstOrDefault(e =>
            e.LogicalName.Equals(pending.RelatedEntity, StringComparison.OrdinalIgnoreCase));
        var select = related is not null && !string.IsNullOrWhiteSpace(related.PrimaryNameAttribute)
            ? new[] { related.PrimaryNameAttribute }
            : Array.Empty<string>();

        return CloneWithLinks(
            pending.Plan,
            [
                new CrmLinkSpec
                {
                    Entity = pending.RelatedEntity,
                    Relationship = schemaName,
                    JoinType = CrmLinkJoinType.Outer,
                    Select = select
                }
            ]);
    }

    public static bool TryMatchClarificationSelection(
        string userMessage,
        PendingRelationshipClarification pending,
        out string schemaName,
        out string? error)
    {
        schemaName = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(userMessage) || pending.Options.Count == 0)
        {
            error = "No pending relationship options.";
            return false;
        }

        var text = userMessage.Trim();
        if (int.TryParse(text, out var index))
        {
            var byIndex = pending.Options.FirstOrDefault(o => o.Index == index);
            if (byIndex is not null)
            {
                schemaName = byIndex.SchemaName;
                return true;
            }

            error = $"Option {index} is not available. Choose 1–{pending.Options.Count}.";
            return false;
        }

        // Match by friendly description (case-insensitive contains / equals).
        var matches = pending.Options
            .Where(o =>
                o.Description.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                o.Description.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                text.Contains(o.Description, StringComparison.OrdinalIgnoreCase) ||
                o.SchemaName.Equals(text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            schemaName = matches[0].SchemaName;
            return true;
        }

        if (matches.Count > 1)
        {
            error = "That selection matches multiple relationships. Reply with the option number.";
            return false;
        }

        error = null; // not a selection — caller continues normal chat
        return false;
    }

    private static CrmQueryPlan CloneWithLinks(CrmQueryPlan plan, IReadOnlyList<CrmLinkSpec> links) =>
        new()
        {
            Entity = plan.Entity,
            Operation = plan.Operation,
            Select = plan.Select,
            Filters = plan.Filters,
            RelativeDate = plan.RelativeDate,
            Sort = plan.Sort,
            ClientSideSort = plan.ClientSideSort,
            Limit = plan.Limit,
            RelatedFromEntity = plan.RelatedFromEntity,
            RelatedFromId = plan.RelatedFromId,
            Aggregates = plan.Aggregates,
            GroupBy = plan.GroupBy,
            Links = links
        };

    private static IEnumerable<CrmRelationshipMetadata> EnumerateApprovedPaths(
        CrmEntityMetadata a,
        CrmEntityMetadata b)
    {
        foreach (var r in a.Relationships.Where(r => r.IsApproved && r.Kind != CrmRelationshipKind.ManyToMany))
        {
            if (Relates(r, a.LogicalName, b.LogicalName))
            {
                yield return r;
            }
        }

        foreach (var r in b.Relationships.Where(r => r.IsApproved && r.Kind != CrmRelationshipKind.ManyToMany))
        {
            if (Relates(r, a.LogicalName, b.LogicalName))
            {
                yield return r;
            }
        }
    }

    private static bool Relates(CrmRelationshipMetadata r, string x, string y) =>
        (r.ReferencingEntity.Equals(x, StringComparison.OrdinalIgnoreCase) &&
         r.ReferencedEntity.Equals(y, StringComparison.OrdinalIgnoreCase)) ||
        (r.ReferencingEntity.Equals(y, StringComparison.OrdinalIgnoreCase) &&
         r.ReferencedEntity.Equals(x, StringComparison.OrdinalIgnoreCase));

    private static string Friendly(string logical) => logical.ToLowerInvariant() switch
    {
        "salesorder" => "Sales Order",
        "account" => "Account",
        "contact" => "Contact",
        "lead" => "Lead",
        _ => logical
    };
}
