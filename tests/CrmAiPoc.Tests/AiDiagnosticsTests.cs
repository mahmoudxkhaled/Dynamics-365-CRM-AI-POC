using System.Text;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class AiDiagnosticsTests
{
    private sealed class TestEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "CrmAiPoc.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static PipelineDiagnostics Create(
        AiDiagnosticsOptions options,
        string environment = "Development")
    {
        return new PipelineDiagnostics(
            Options.Create(options).ToMonitor(),
            new TestEnv(environment),
            NullLogger<PipelineDiagnostics>.Instance);
    }

    [Fact]
    public void Defaults_Are_Disabled()
    {
        var opts = new AiDiagnosticsOptions();
        Assert.False(opts.Enabled);
        Assert.False(opts.AllowSensitivePayloadLogging);
    }

    [Fact]
    public void Correlation_Scopes_Are_Isolated()
    {
        Assert.Null(DiagnosticsCorrelation.Current);
        using (DiagnosticsCorrelation.Begin("abc123def456"))
        {
            Assert.Equal("abc123def456", DiagnosticsCorrelation.Current);
            using (DiagnosticsCorrelation.Begin("nested999999"))
            {
                Assert.Equal("nested999999", DiagnosticsCorrelation.Current);
            }

            Assert.Equal("abc123def456", DiagnosticsCorrelation.Current);
        }

        Assert.Null(DiagnosticsCorrelation.Current);
    }

    [Fact]
    public void Sensitive_Payloads_Require_Development_OptIn()
    {
        var diagDev = Create(new AiDiagnosticsOptions
        {
            Enabled = true,
            AllowSensitivePayloadLogging = true
        }, "Development");
        Assert.True(diagDev.AllowSensitivePayloads);
        Assert.Equal("secret question", diagDev.SanitizeUserQuestion("secret question"));

        var diagProd = Create(new AiDiagnosticsOptions
        {
            Enabled = true,
            AllowSensitivePayloadLogging = true
        }, "Production");
        Assert.False(diagProd.AllowSensitivePayloads);
        Assert.Contains("redacted", diagProd.SanitizeUserQuestion("secret question"));
    }

    [Fact]
    public void SanitizeCrmUrl_Masks_FetchXml_Parameter()
    {
        var diag = Create(new AiDiagnosticsOptions { Enabled = true });
        var url = "https://crm.example/api/data/v9.1/salesorders?fetchXml=%3Cfetch%3Esecret%3C%2Ffetch%3E&foo=1";
        var sanitized = diag.SanitizeCrmUrl(url);
        Assert.Contains("[logged separately in FETCHXML]", sanitized);
        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("foo=1", sanitized);
    }

    [Fact]
    public void FormatFetchXml_Redacts_NonDate_Values_By_Default()
    {
        var diag = Create(new AiDiagnosticsOptions { Enabled = true });
        var xml = """
            <fetch>
              <entity name="salesorder">
                <filter>
                  <condition attribute="name" operator="like" value="%Contoso%" />
                  <condition attribute="createdon" operator="ge" value="2025-01-01T00:00:00.000Z" />
                </filter>
              </entity>
            </fetch>
            """;
        var logged = diag.FormatFetchXmlForLog(xml);
        Assert.Contains("***", logged);
        Assert.Contains("2025-01-01T00:00:00.000Z", logged);
        Assert.DoesNotContain("Contoso", logged);
    }

    [Fact]
    public void ClassifyCrmRequest_Distinguishes_FetchXml_And_OData()
    {
        var diag = Create(new AiDiagnosticsOptions { Enabled = true });
        Assert.Equal(
            CrmRequestCategory.BusinessFetchXml,
            diag.ClassifyCrmRequest("salesorders?fetchXml=%3Cfetch"));
        Assert.Equal(
            CrmRequestCategory.WhoAmI,
            diag.ClassifyCrmRequest("WhoAmI()"));
        Assert.Equal(
            CrmRequestCategory.MetadataDiscovery,
            diag.ClassifyCrmRequest("EntityDefinitions(LogicalName='salesorder')?$select=LogicalName"));
    }

    [Fact]
    public void Logging_Failures_Do_Not_Throw()
    {
        var diag = Create(new AiDiagnosticsOptions
        {
            Enabled = true,
            LogMetadata = true,
            MaxSectionChars = 50
        });

        var ex = Record.Exception(() =>
            diag.LogMetadata(_ => throw new InvalidOperationException("writer failed")));
        Assert.Null(ex);
    }

    [Fact]
    public void Truncation_Is_Explicit()
    {
        var diag = Create(new AiDiagnosticsOptions
        {
            Enabled = true,
            MaxSectionChars = 80
        });
        var body = new string('a', 200);
        var limited = diag.ApplySectionBudget(body);
        Assert.Contains("[TRUNCATED:", limited);
        Assert.True(limited.Length < body.Length + 80);
    }
}

internal static class OptionsMonitorExtensions
{
    public static IOptionsMonitor<T> ToMonitor<T>(this IOptions<T> options) where T : class, new() =>
        new StaticOptionsMonitor<T>(options.Value);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
