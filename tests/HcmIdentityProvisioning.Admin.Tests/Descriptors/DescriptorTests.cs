using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Descriptors;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HcmIdentityProvisioning.Admin.Tests.Descriptors;

public class DescriptorTests
{
    [Fact]
    public void GenericRestDescriptor_ConfigureServices_RegistersRestConnectorWhenOptionsGiven()
    {
        var descriptor = new GenericRestHcmConnectorCliDescriptor();
        var services = new ServiceCollection();

        // Create a root command with the options so we can parse arguments
        var root = new RootCommand();
        foreach (var opt in descriptor.GetCliOptions())
        {
            root.AddOption(opt);
        }

        var parseResult = root.Parse("--rest-url https://api.example.com --rest-token test-token-123");
        var baseOptions = new SyncCliOptions();

        descriptor.ConfigureServices(services, parseResult, baseOptions);

        var sp = services.BuildServiceProvider();
        var connector = sp.GetService<IHcmConnector>();
        connector.Should().NotBeNull();
        connector.Should().BeOfType<GenericRestHcmConnector>();

        var options = sp.GetRequiredService<IOptions<RestHcmConnectorOptions>>().Value;
        options.BaseUrl.Should().Be("https://api.example.com");
        options.BearerToken.Should().Be("test-token-123");
        options.AuthScheme.Should().Be(HcmAuthScheme.Bearer);
    }

    [Fact]
    public void GenericRestDescriptor_ConfigureServices_DoesNotRegisterWhenNoOptionsGiven()
    {
        var descriptor = new GenericRestHcmConnectorCliDescriptor();
        var services = new ServiceCollection();

        var root = new RootCommand();
        foreach (var opt in descriptor.GetCliOptions())
        {
            root.AddOption(opt);
        }

        var parseResult = root.Parse("");
        var baseOptions = new SyncCliOptions();

        descriptor.ConfigureServices(services, parseResult, baseOptions);

        var sp = services.BuildServiceProvider();
        var connector = sp.GetService<IHcmConnector>();
        connector.Should().BeNull();
    }

    [Fact]
    public void SyntheticDescriptor_ConfigureServices_RegistersSyntheticConnector()
    {
        var descriptor = new SyntheticHcmConnectorCliDescriptor();
        var services = new ServiceCollection();

        var root = new RootCommand();
        foreach (var opt in descriptor.GetCliOptions())
        {
            root.AddOption(opt);
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "[]");
            var parseResult = root.Parse($"--fixtures \"{tempFile}\"");
            var baseOptions = new SyncCliOptions(FixturesPath: tempFile);

            descriptor.ConfigureServices(services, parseResult, baseOptions);

            var sp = services.BuildServiceProvider();
            var connector = sp.GetService<IHcmConnector>();
            connector.Should().NotBeNull();
            connector.Should().BeOfType<SyntheticHcmConnector>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
