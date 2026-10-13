using FluentAssertions;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.DependencyInjection;

public sealed class ProductionAdaptersDependencyInjectionTests
{
    [Fact]
    public void AddEntraIdGraphAdapter_WithOptions_ResolvesEntraIdGraphAdapterAsIdentityStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddEntraIdGraphAdapter(options =>
        {
            options.TenantDomain = "company.onmicrosoft.com";
            options.ManagedGroupPrefix = "grp-custom-";
            options.FilterChunkSize = 10;
            options.MaxBatchSize = 15;
            options.CustomCredential = new MockTokenCredential();
        });

        // Act
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IIdentityStore>();
        var options = sp.GetRequiredService<IOptions<EntraIdGraphOptions>>().Value;

        // Assert
        store.Should().NotBeNull();
        store.Should().BeOfType<EntraIdGraphAdapter>();
        options.TenantDomain.Should().Be("company.onmicrosoft.com");
        options.ManagedGroupPrefix.Should().Be("grp-custom-");
        options.FilterChunkSize.Should().Be(10);
        options.MaxBatchSize.Should().Be(15);
    }

    [Fact]
    public void AddEntraIdGraphAdapter_WithoutOptions_ResolvesWithDefaultOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddEntraIdGraphAdapter();

        // Act
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IIdentityStore>();
        var options = sp.GetRequiredService<IOptions<EntraIdGraphOptions>>().Value;

        // Assert
        store.Should().NotBeNull();
        store.Should().BeOfType<EntraIdGraphAdapter>();
        options.ManagedGroupPrefix.Should().Be("grp-iam-");
        options.MaxBatchSize.Should().Be(20);
        options.FilterChunkSize.Should().Be(15);
    }

    [Fact]
    public void AddEntraIdGraphAdapter_WithCustomHttpClientInContainer_ResolvesAdapterSuccessfully()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var mockHandler = new MockHttpMessageHandler();
        services.AddSingleton(mockHandler.ToHttpClient());

        services.AddEntraIdGraphAdapter(options =>
        {
            options.TenantDomain = "company.onmicrosoft.com";
            options.CustomCredential = new MockTokenCredential();
        });

        // Act
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IIdentityStore>();

        // Assert
        store.Should().NotBeNull();
        store.Should().BeOfType<EntraIdGraphAdapter>();
    }

    [Fact]
    public void AddGenericRestHcmConnector_WithDefaultMapper_ResolvesGenericRestHcmConnectorAndDefaultMapper()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddGenericRestHcmConnector(options =>
        {
            options.BaseUrl = "https://api.hcm.example.com";
            options.Endpoint = "/api/v2/staff";
            options.AuthScheme = HcmAuthScheme.Bearer;
            options.BearerToken = "sample-secret-bearer";
            options.TimeoutSeconds = 45;
        });

        // Act
        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>();
        var mapper = sp.GetRequiredService<IHcmPayloadMapper>();
        var options = sp.GetRequiredService<IOptions<RestHcmConnectorOptions>>().Value;

        // Assert
        connector.Should().NotBeNull();
        connector.Should().BeOfType<GenericRestHcmConnector>();
        mapper.Should().NotBeNull();
        mapper.Should().BeOfType<DefaultHcmPayloadMapper>();
        options.BaseUrl.Should().Be("https://api.hcm.example.com");
        options.Endpoint.Should().Be("/api/v2/staff");
        options.AuthScheme.Should().Be(HcmAuthScheme.Bearer);
        options.BearerToken.Should().Be("sample-secret-bearer");
        options.TimeoutSeconds.Should().Be(45);
    }

    [Fact]
    public void AddGenericRestHcmConnector_WithCustomMapper_ResolvesGenericRestHcmConnectorAndCustomMapper()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddGenericRestHcmConnector<CustomTestMapper>(options =>
        {
            options.BaseUrl = "https://custom-erp.company.corp";
            options.Endpoint = "/export/users";
            options.AuthScheme = HcmAuthScheme.ApiKey;
            options.ApiKey = "key-abc-123";
            options.ApiKeyHeaderName = "X-Corp-Key";
        });

        // Act
        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>();
        var mapper = sp.GetRequiredService<IHcmPayloadMapper>();
        var options = sp.GetRequiredService<IOptions<RestHcmConnectorOptions>>().Value;

        // Assert
        connector.Should().NotBeNull();
        connector.Should().BeOfType<GenericRestHcmConnector>();
        mapper.Should().NotBeNull();
        mapper.Should().BeOfType<CustomTestMapper>();
        options.BaseUrl.Should().Be("https://custom-erp.company.corp");
        options.Endpoint.Should().Be("/export/users");
        options.AuthScheme.Should().Be(HcmAuthScheme.ApiKey);
        options.ApiKey.Should().Be("key-abc-123");
        options.ApiKeyHeaderName.Should().Be("X-Corp-Key");
    }

    [Fact]
    public void AddGenericRestHcmConnector_WithoutOptions_ResolvesWithDefaultOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddGenericRestHcmConnector();

        // Act
        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>();
        var mapper = sp.GetRequiredService<IHcmPayloadMapper>();
        var options = sp.GetRequiredService<IOptions<RestHcmConnectorOptions>>().Value;

        // Assert
        connector.Should().NotBeNull();
        connector.Should().BeOfType<GenericRestHcmConnector>();
        mapper.Should().NotBeNull();
        mapper.Should().BeOfType<DefaultHcmPayloadMapper>();
        options.Endpoint.Should().Be("/api/v1/employees");
        options.AuthScheme.Should().Be(HcmAuthScheme.Bearer);
        options.TimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void AddGenericRestHcmConnector_ConfiguresHttpClientBaseAddressFromOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddGenericRestHcmConnector(options =>
        {
            options.BaseUrl = "https://api.hcm.example.com/";
        });

        // Act
        var sp = services.BuildServiceProvider();
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient(nameof(GenericRestHcmConnector));

        // Assert
        httpClient.Should().NotBeNull();
        httpClient.BaseAddress.Should().Be(new Uri("https://api.hcm.example.com/"));
    }

    private sealed class CustomTestMapper : IHcmPayloadMapper
    {
        public string BuildPageUri(string endpoint, int pageNumber, int pageSize)
            => $"{endpoint}?page={pageNumber}&size={pageSize}";

        public Task<PagedResult<Employee>> MapResponseAsync(
            HttpResponseMessage response,
            int pageNumber,
            int pageSize,
            CancellationToken ct = default)
        {
            return Task.FromResult(new PagedResult<Employee>(
                Array.Empty<Employee>(),
                pageNumber,
                pageSize,
                0,
                false));
        }
    }
}
