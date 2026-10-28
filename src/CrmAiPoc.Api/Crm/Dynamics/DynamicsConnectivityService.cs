using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Dynamics;

public sealed class DynamicsConnectivityService
{
    private readonly DynamicsCrmHttp _http;
    private readonly CrmOptions _options;
    private readonly ILogger<DynamicsConnectivityService> _logger;

    public DynamicsConnectivityService(
        DynamicsCrmHttp http,
        IOptions<CrmOptions> options,
        ILogger<DynamicsConnectivityService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WhoAmIDiagnosticResult> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        DynamicsHttpResult httpResult;
        try
        {
            httpResult = await _http.GetAsync("WhoAmI()", cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new WhoAmIDiagnosticResult
            {
                Success = false,
                AuthMode = _http.DescribeAuthMode(),
                BaseUrlConfigured = !string.IsNullOrWhiteSpace(_options.BaseUrl),
                EnableLiveIntegration = _options.EnableLiveIntegration,
                ErrorCategory = "configuration",
                Message = ex.Message,
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            };
        }

        if (!httpResult.IsSuccess)
        {
            return new WhoAmIDiagnosticResult
            {
                Success = false,
                StatusCode = httpResult.StatusCode,
                AuthMode = _http.DescribeAuthMode(),
                BaseUrlConfigured = true,
                EnableLiveIntegration = _options.EnableLiveIntegration,
                ErrorCategory = httpResult.ErrorCategory,
                Message = httpResult.ExceptionMessage
                    ?? $"WhoAmI failed with HTTP {httpResult.StatusCode} ({httpResult.ErrorCategory}).",
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            };
        }

        using var doc = JsonDocument.Parse(httpResult.Body);
        var root = doc.RootElement;

        var result = new WhoAmIDiagnosticResult
        {
            Success = true,
            StatusCode = httpResult.StatusCode,
            AuthMode = _http.DescribeAuthMode(),
            BaseUrlConfigured = true,
            EnableLiveIntegration = _options.EnableLiveIntegration,
            UserId = GetGuid(root, "UserId"),
            BusinessUnitId = GetGuid(root, "BusinessUnitId"),
            OrganizationId = GetGuid(root, "OrganizationId"),
            Message = "Authenticated WhoAmI succeeded.",
            DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
        };

        _logger.LogInformation(
            "WhoAmI succeeded. UserId={UserId}, BusinessUnitId={BusinessUnitId}, OrganizationId={OrganizationId}",
            result.UserId,
            result.BusinessUnitId,
            result.OrganizationId);

        return result;
    }

    public async Task<CrmIdentityAuditResult> AuditIdentityAsync(
        ClaimsPrincipal? requestUser,
        CancellationToken cancellationToken = default)
    {
        var processIdentity = WindowsIdentity.GetCurrent();
        var processAccount = processIdentity.Name;
        var processSid = processIdentity.User?.Value;

        string? requestAccount = null;
        string? requestAuthType = null;
        var requestAuthenticated = false;
        if (requestUser?.Identity is { } identity)
        {
            requestAuthenticated = identity.IsAuthenticated;
            requestAccount = identity.Name;
            requestAuthType = identity.AuthenticationType;
        }

        var who = await WhoAmIAsync(cancellationToken);
        string? crmDomainName = null;
        string? crmFullName = null;
        string? crmEmail = null;
        bool? crmDisabled = null;
        string? lookupMessage = null;

        if (who.Success && who.UserId is Guid userId)
        {
            // Read-only systemuser projection for identity verification (no passwords/tokens).
            var path =
                $"systemusers({userId:D})?$select=systemuserid,domainname,fullname,internalemailaddress,isdisabled";
            var http = await _http.GetAsync(path, cancellationToken);
            if (http.IsSuccess)
            {
                using var doc = JsonDocument.Parse(http.Body);
                var root = doc.RootElement;
                crmDomainName = GetString(root, "domainname");
                crmFullName = GetString(root, "fullname");
                crmEmail = GetString(root, "internalemailaddress");
                if (root.TryGetProperty("isdisabled", out var disabled) &&
                    (disabled.ValueKind is JsonValueKind.True or JsonValueKind.False))
                {
                    crmDisabled = disabled.GetBoolean();
                }

                lookupMessage = "systemuser lookup succeeded.";
            }
            else
            {
                lookupMessage =
                    $"systemuser lookup failed: HTTP {http.StatusCode} ({http.ErrorCategory}).";
            }
        }

        var processMatchesCrm =
            !string.IsNullOrWhiteSpace(processAccount) &&
            !string.IsNullOrWhiteSpace(crmDomainName) &&
            string.Equals(processAccount, crmDomainName, StringComparison.OrdinalIgnoreCase);

        var explanation =
            "CRM HTTP calls use HttpClientHandler with UseDefaultCredentials / CredentialCache.DefaultCredentials. " +
            "For a local interactive `dotnet run`, that is the Windows account of the process " +
            $"(currently '{processAccount}'), not an anonymous caller and not a stored AD password. " +
            "ASP.NET request identity is separate and is only populated if the host authenticates the HTTP caller " +
            "(e.g. Windows Auth). Kestrel over plain HTTP often has no authenticated request user.";

        return new CrmIdentityAuditResult
        {
            Success = who.Success && processMatchesCrm,
            AuthMode = who.AuthMode,
            ProcessWindowsAccount = processAccount,
            ProcessWindowsSid = processSid,
            RequestWindowsAccount = requestAccount,
            RequestAuthenticationType = requestAuthType,
            RequestIsAuthenticated = requestAuthenticated,
            CrmUserId = who.UserId,
            CrmBusinessUnitId = who.BusinessUnitId,
            CrmOrganizationId = who.OrganizationId,
            CrmDomainName = crmDomainName,
            CrmFullName = crmFullName,
            CrmEmail = crmEmail,
            CrmIsDisabled = crmDisabled,
            ProcessAccountMatchesCrmDomainName = processMatchesCrm,
            WhoAmIMessage = who.Message,
            SystemUserLookupMessage = lookupMessage,
            Explanation = explanation,
            MultiUserDeploymentNotes =
                "Before multi-user deployment: (1) Host behind IIS/HTTP.sys with Windows Authentication. " +
                "(2) Impersonate/delegate the caller to CRM (usually Kerberos constrained delegation) so DefaultCredentials " +
                "are the end-user, not the app pool. (3) Enforce CRM security roles per user via WhoAmI + CRM privileges; " +
                "do not treat client-supplied role names as auth. (4) Keep AllowSendLiveDataToAi=false so Groq never receives CRM rows."
        };
    }

    public async Task<EntityDefinitionDiagnosticResult> GetSalesOrderEntityDefinitionAsync(
        CancellationToken cancellationToken = default)
    {
        const string path =
            "EntityDefinitions(LogicalName='salesorder')?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute";

        var started = DateTimeOffset.UtcNow;
        DynamicsHttpResult httpResult;
        try
        {
            httpResult = await _http.GetAsync(path, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new EntityDefinitionDiagnosticResult
            {
                Success = false,
                ErrorCategory = "configuration",
                Message = ex.Message,
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            };
        }

        if (!httpResult.IsSuccess)
        {
            return new EntityDefinitionDiagnosticResult
            {
                Success = false,
                StatusCode = httpResult.StatusCode,
                ErrorCategory = httpResult.ErrorCategory,
                Message = httpResult.ExceptionMessage
                    ?? $"EntityDefinitions failed with HTTP {httpResult.StatusCode}.",
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            };
        }

        using var doc = JsonDocument.Parse(httpResult.Body);
        var root = doc.RootElement;

        return new EntityDefinitionDiagnosticResult
        {
            Success = true,
            StatusCode = httpResult.StatusCode,
            LogicalName = GetString(root, "LogicalName"),
            EntitySetName = GetString(root, "EntitySetName"),
            PrimaryIdAttribute = GetString(root, "PrimaryIdAttribute"),
            PrimaryNameAttribute = GetString(root, "PrimaryNameAttribute"),
            Message = "salesorder EntityDefinitions read succeeded.",
            DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds
        };
    }

    private static Guid? GetGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.TryGetGuid(out var guid) ? guid : null;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}

public sealed class WhoAmIDiagnosticResult
{
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string? AuthMode { get; init; }
    public bool BaseUrlConfigured { get; init; }
    public bool EnableLiveIntegration { get; init; }
    public Guid? UserId { get; init; }
    public Guid? BusinessUnitId { get; init; }
    public Guid? OrganizationId { get; init; }
    public string? ErrorCategory { get; init; }
    public string? Message { get; init; }
    public double DurationMs { get; init; }
}

public sealed class CrmIdentityAuditResult
{
    public bool Success { get; init; }
    public string? AuthMode { get; init; }
    public string? ProcessWindowsAccount { get; init; }
    public string? ProcessWindowsSid { get; init; }
    public string? RequestWindowsAccount { get; init; }
    public string? RequestAuthenticationType { get; init; }
    public bool RequestIsAuthenticated { get; init; }
    public Guid? CrmUserId { get; init; }
    public Guid? CrmBusinessUnitId { get; init; }
    public Guid? CrmOrganizationId { get; init; }
    public string? CrmDomainName { get; init; }
    public string? CrmFullName { get; init; }
    public string? CrmEmail { get; init; }
    public bool? CrmIsDisabled { get; init; }
    public bool ProcessAccountMatchesCrmDomainName { get; init; }
    public string? WhoAmIMessage { get; init; }
    public string? SystemUserLookupMessage { get; init; }
    public string? Explanation { get; init; }
    public string? MultiUserDeploymentNotes { get; init; }
}

public sealed class EntityDefinitionDiagnosticResult
{
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string? LogicalName { get; init; }
    public string? EntitySetName { get; init; }
    public string? PrimaryIdAttribute { get; init; }
    public string? PrimaryNameAttribute { get; init; }
    public string? ErrorCategory { get; init; }
    public string? Message { get; init; }
    public double DurationMs { get; init; }
}
