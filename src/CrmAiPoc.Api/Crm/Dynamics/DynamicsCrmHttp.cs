using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Dynamics;

/// <summary>
/// Read-only Dynamics Web API HTTP helper. GET only. Never logs credentials.
/// </summary>
public sealed class DynamicsCrmHttp
{
    public const string HttpClientName = "DynamicsCrm";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CrmOptions _options;
    private readonly ILogger<DynamicsCrmHttp> _logger;

    public DynamicsCrmHttp(
        IHttpClientFactory httpClientFactory,
        IOptions<CrmOptions> options,
        ILogger<DynamicsCrmHttp> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public void EnsureLiveConfigured()
    {
        if (!_options.EnableLiveIntegration)
        {
            throw new InvalidOperationException(
                "Dynamics live integration is disabled. Set Crm:EnableLiveIntegration=true and Crm:BaseUrl.");
        }

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("Crm:BaseUrl is required for Dynamics connectivity.");
        }
    }

    public Task<DynamicsHttpResult> GetAsync(string relativePath, CancellationToken cancellationToken = default) =>
        GetAsync(relativePath, includeAnnotations: false, cancellationToken);

    /// <param name="includeAnnotations">
    /// When true, sends Prefer odata.include-annotations="*" (used for count limit-exceeded flags).
    /// Keep false for general list queries — some On-Prem paths historically failed with Prefer.
    /// </param>
    public async Task<DynamicsHttpResult> GetAsync(
        string relativePath,
        bool includeAnnotations,
        CancellationToken cancellationToken = default)
    {
        EnsureLiveConfigured();

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = Combine(_options.BaseUrl!, relativePath);

        _logger.LogInformation(
            "Dynamics GET (read-only): Path={Path}, AuthMode={AuthMode}, Annotations={Annotations}",
            relativePath,
            DescribeAuthMode(),
            includeAnnotations);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
        request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        if (includeAnnotations)
        {
            request.Headers.TryAddWithoutValidation("Prefer", "odata.include-annotations=\"*\"");
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Dynamics GET failed: Status={StatusCode}, Path={Path}, BodyLength={BodyLength}, Category={Category}",
                    (int)response.StatusCode,
                    relativePath,
                    body.Length,
                    Categorize((int)response.StatusCode));
            }

            return new DynamicsHttpResult
            {
                StatusCode = (int)response.StatusCode,
                IsSuccess = response.IsSuccessStatusCode,
                Body = body,
                Path = relativePath,
                ErrorCategory = response.IsSuccessStatusCode ? null : Categorize((int)response.StatusCode)
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Dynamics network/HTTP failure for Path={Path}", relativePath);
            return new DynamicsHttpResult
            {
                StatusCode = 0,
                IsSuccess = false,
                Body = string.Empty,
                Path = relativePath,
                ErrorCategory = "network",
                ExceptionMessage = SanitizeExceptionMessage(ex)
            };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Dynamics request timed out for Path={Path}", relativePath);
            return new DynamicsHttpResult
            {
                StatusCode = 0,
                IsSuccess = false,
                Body = string.Empty,
                Path = relativePath,
                ErrorCategory = "timeout",
                ExceptionMessage = "Request timed out reaching Dynamics Web API."
            };
        }
    }

    public string DescribeAuthMode()
    {
        if (_options.HasExplicitNetworkCredentials)
        {
            var user = _options.Username ?? "(user)";
            var domain = string.IsNullOrWhiteSpace(_options.Domain) ? "(default-domain)" : _options.Domain;
            return $"explicit-network-credential:{domain}\\{user}";
        }

        if (_options.UseDefaultCredentials)
        {
            return "windows-default-credentials";
        }

        return "none-configured";
    }

    public static HttpMessageHandler CreateHandler(CrmOptions options)
    {
        var handler = new HttpClientHandler
        {
            PreAuthenticate = true,
            // SSL validation remains enabled (do not set ServerCertificateCustomValidationCallback).
            CheckCertificateRevocationList = true
        };

        if (options.HasExplicitNetworkCredentials)
        {
            handler.UseDefaultCredentials = false;
            handler.Credentials = new NetworkCredential(
                options.Username,
                options.Password,
                options.Domain);
        }
        else if (options.UseDefaultCredentials)
        {
            handler.UseDefaultCredentials = true;
            handler.Credentials = CredentialCache.DefaultCredentials;
        }

        return handler;
    }

    private static string Combine(string baseUrl, string relative)
    {
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return baseUrl + relative.TrimStart('/');
    }

    private static string Categorize(int statusCode) => statusCode switch
    {
        0 => "network",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not-found",
        >= 500 => "server-error",
        _ => "http-error"
    };

    private static string SanitizeExceptionMessage(Exception ex)
    {
        var message = ex.Message;
        // Defensive: never echo credential-like substrings if present in nested messages.
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Password", StringComparison.Ordinal))
        {
            return "Network/HTTP failure contacting Dynamics (details redacted).";
        }

        return message;
    }
}

public sealed class DynamicsHttpResult
{
    public required int StatusCode { get; init; }
    public required bool IsSuccess { get; init; }
    public required string Body { get; init; }
    public required string Path { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ExceptionMessage { get; init; }
}
