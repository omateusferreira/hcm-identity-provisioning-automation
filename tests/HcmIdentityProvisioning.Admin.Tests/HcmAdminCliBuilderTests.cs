using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Descriptors;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HcmIdentityProvisioning.Admin.Tests;

public class HcmAdminCliBuilderTests
{
    private class MockCustomDescriptor : IHcmConnectorCliDescriptor
    {
        public string ConnectorKey => "mock";
        public string Description => "Mock Connector for Testing";
        public Option<string> MockUrlOption { get; } = new("--mock-url", "Mock URL option");

        public IReadOnlyList<Option> GetCliOptions() => [MockUrlOption];

        public void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions)
        {
        }
    }

    private class TestPrompter : IConsolePrompter
    {
        public bool IsInputRedirected => false;
        public bool Confirm(string message) => true;
        public Task<bool> ConfirmAsync(string prompt, bool defaultResponse = false) => Task.FromResult(true);
        public Task<string> PromptAsync(string prompt, string? defaultValue = null) => Task.FromResult("test");
        public string ReadMaskedPassword(string prompt) => "secret";
    }

    [Fact]
    public async Task RunAsync_WhenHelpRequested_OutputsOptionsIncludingCustomDescriptorOptions()
    {
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);

            var builder = HcmAdminCliBuilder.Create(["sync", "--help"])
                .AddConnector<MockCustomDescriptor>();

            var exitCode = await builder.RunAsync();
            exitCode.Should().Be(0);

            var output = sw.ToString();
            output.Should().Contain("--dry-run");
            output.Should().Contain("--mock-url");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_WithCustomCommand_ExecutesSuccessfully()
    {
        bool customExecuted = false;
        var customCmd = new Command("custom-ping", "Custom Ping Command");
        customCmd.SetHandler(() => { customExecuted = true; return Task.CompletedTask; });

        var builder = HcmAdminCliBuilder.Create(["custom-ping"])
            .AddCommand(customCmd);

        var exitCode = await builder.RunAsync();
        exitCode.Should().Be(0);
        customExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithValidateRules_ExecutesSuccessfully()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? rulesPath = null;
        while (currentDir != null)
        {
            var candidate = Path.Combine(currentDir.FullName, "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json");
            if (File.Exists(candidate))
            {
                rulesPath = candidate;
                break;
            }
            currentDir = currentDir.Parent;
        }

        rulesPath.Should().NotBeNull("rules.json must exist in repository");

        var builder = HcmAdminCliBuilder.Create(["validate-rules", "--rules", rulesPath!]);
        var exitCode = await builder.RunAsync();
        exitCode.Should().Be(0);
    }

    [Fact]
    public void BuildServiceProvider_WhenNoConnectorConfigured_FallsBackToSyntheticConnector()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? rulesPath = null;
        string? fixturesPath = null;
        while (currentDir != null)
        {
            var r = Path.Combine(currentDir.FullName, "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json");
            if (File.Exists(r) && rulesPath == null) rulesPath = r;

            var f = Path.Combine(currentDir.FullName, "fixtures", "synthetic-employees.json");
            if (File.Exists(f) && fixturesPath == null) fixturesPath = f;

            currentDir = currentDir.Parent;
        }

        var builder = HcmAdminCliBuilder.Create([]);
        var options = new SyncCliOptions(
            RulesPath: rulesPath,
            FixturesPath: fixturesPath,
            Idp: "in-memory"
        );

        var sp = builder.BuildServiceProvider(options);
        var connector = sp.GetRequiredService<IHcmConnector>();
        connector.Should().BeOfType<SyntheticHcmConnector>();
    }

    [Fact]
    public void BuildServiceProvider_WithCustomServiceConfigurator_AppliesConfigurations()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? rulesPath = null;
        while (currentDir != null)
        {
            var r = Path.Combine(currentDir.FullName, "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json");
            if (File.Exists(r)) { rulesPath = r; break; }
            currentDir = currentDir.Parent;
        }

        bool serviceConfigured = false;
        var builder = HcmAdminCliBuilder.Create([])
            .ConfigureServices(services =>
            {
                serviceConfigured = true;
                services.AddSingleton("test-service-registered");
            });

        var options = new SyncCliOptions(RulesPath: rulesPath);
        var sp = builder.BuildServiceProvider(options);

        serviceConfigured.Should().BeTrue();
        sp.GetRequiredService<string>().Should().Be("test-service-registered");
    }

    [Fact]
    public void WithPrompter_AcceptsCustomPrompter()
    {
        var prompter = new TestPrompter();
        var builder = HcmAdminCliBuilder.Create([])
            .WithPrompter(prompter);

        builder.Should().NotBeNull();
    }

    [Fact]
    public void NativeDescriptors_ExposeExpectedKeysAndOptions()
    {
        var synthetic = new SyntheticHcmConnectorCliDescriptor();
        synthetic.ConnectorKey.Should().Be("synthetic");
        synthetic.GetCliOptions().Should().ContainSingle(o => o.Aliases.Contains("--fixtures"));

        var rest = new GenericRestHcmConnectorCliDescriptor();
        rest.ConnectorKey.Should().Be("rest");
        rest.GetCliOptions().Should().HaveCount(2);
        rest.GetCliOptions().Should().Contain(o => o.Aliases.Contains("--rest-url"));
        rest.GetCliOptions().Should().Contain(o => o.Aliases.Contains("--rest-token"));
    }
}
