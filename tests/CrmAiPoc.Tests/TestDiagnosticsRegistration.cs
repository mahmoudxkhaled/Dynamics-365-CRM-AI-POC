using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CrmAiPoc.Tests;

internal static class TestDiagnosticsRegistration
{
    public static IServiceCollection AddPipelineDiagnosticsForTests(
        this IServiceCollection services,
        bool enabled = false)
    {
        services.Configure<AiDiagnosticsOptions>(o =>
        {
            o.Enabled = enabled;
            o.AllowSensitivePayloadLogging = false;
        });
        services.AddSingleton<IHostEnvironment>(_ => new TestHostEnvironment());
        services.AddSingleton<PipelineDiagnostics>();
        return services;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CrmAiPoc.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
