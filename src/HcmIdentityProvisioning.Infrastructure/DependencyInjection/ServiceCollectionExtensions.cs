using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Connectors.Synthetic;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Policies;
using HcmIdentityProvisioning.Infrastructure.Rules;
using HcmIdentityProvisioning.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHcmProvisioningCore(
        this IServiceCollection services,
        SyncSettings settings,
        string rulesJsonPath)
    {
        services.AddSingleton(settings);
        services.AddSingleton<ISecurePasswordGenerator, SecurePasswordGenerator>();
        services.AddSingleton<ICircuitBreaker>(_ => new DisablementCircuitBreaker(settings.MaxDisablementPercentage, settings.MaxDisablementCount));
        services.AddSingleton<IRulesEngine>(_ => MicrosoftRulesEngineAdapter.FromFile(rulesJsonPath));
        services.AddSingleton<IdentityReconciliationService>();
        services.AddTransient<ReconcileBatchUseCase>();
        services.AddTransient<DryRunAuditUseCase>();

        return services;
    }

    public static IServiceCollection AddSyntheticHcmConnector(
        this IServiceCollection services,
        string fixturesPath)
    {
        services.AddSingleton<IHcmConnector>(_ => SyntheticHcmConnector.FromFixturesFile(fixturesPath));
        return services;
    }

    public static IServiceCollection AddInMemoryIdentityStore(
        this IServiceCollection services,
        Action<InMemoryIdentityStore>? seedAction = null)
    {
        var store = new InMemoryIdentityStore();
        seedAction?.Invoke(store);
        services.AddSingleton<IIdentityStore>(store);
        services.AddSingleton<ICredentialDeliveryService, MockCredentialDeliveryService>();
        return services;
    }

    public static IServiceCollection AddEntraIdGraphAdapter(
        this IServiceCollection services,
        Action<EntraIdGraphOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<EntraIdGraphAdapter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EntraIdGraphOptions>>();
            var logger = sp.GetService<ILogger<EntraIdGraphAdapter>>() ?? NullLogger<EntraIdGraphAdapter>.Instance;
            var graphClient = sp.GetService<GraphServiceClient>();
            var httpClient = sp.GetService<HttpClient>();

            if (graphClient != null)
            {
                return new EntraIdGraphAdapter(options, logger, graphClient);
            }

            if (httpClient != null)
            {
                return new EntraIdGraphAdapter(options, logger, httpClient);
            }

            return new EntraIdGraphAdapter(options, logger);
        });

        services.AddSingleton<IIdentityStore>(sp => sp.GetRequiredService<EntraIdGraphAdapter>());

        return services;
    }

    public static IServiceCollection AddGenericRestHcmConnector(
        this IServiceCollection services,
        Action<RestHcmConnectorOptions>? configureOptions = null)
    {
        return services.AddGenericRestHcmConnector<DefaultHcmPayloadMapper>(configureOptions);
    }

    public static IServiceCollection AddGenericRestHcmConnector<TMapper>(
        this IServiceCollection services,
        Action<RestHcmConnectorOptions>? configureOptions = null)
        where TMapper : class, IHcmPayloadMapper
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<IHcmPayloadMapper, TMapper>();

        var httpClientBuilder = services.AddHttpClient(nameof(GenericRestHcmConnector), (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RestHcmConnectorOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                var normalizedBase = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
                client.BaseAddress = new Uri(normalizedBase);
            }

            if (options.TimeoutSeconds > 0)
            {
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            }
        });

        httpClientBuilder.AddStandardResilienceHandler(resilienceOptions =>
        {
            resilienceOptions.Retry.MaxRetryAttempts = 3;
            resilienceOptions.CircuitBreaker.FailureRatio = 0.5;
            resilienceOptions.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            resilienceOptions.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            resilienceOptions.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddTransient<GenericRestHcmConnector>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(GenericRestHcmConnector));
            var mapper = sp.GetRequiredService<IHcmPayloadMapper>();
            var options = sp.GetRequiredService<IOptions<RestHcmConnectorOptions>>();
            return new GenericRestHcmConnector(httpClient, mapper, options);
        });

        services.AddTransient<IHcmConnector>(sp => sp.GetRequiredService<GenericRestHcmConnector>());

        return services;
    }

    public static IServiceCollection AddGraphEmailCredentialDeliveryService(
        this IServiceCollection services,
        Action<GraphEmailDeliveryOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.AddSingleton<GraphEmailCredentialDeliveryService>(sp =>
        {
            var graphClient = sp.GetService<GraphServiceClient>()
                ?? sp.GetService<EntraIdGraphAdapter>()?.GraphClient;

            if (graphClient == null)
            {
                var entraOptions = sp.GetService<IOptions<EntraIdGraphOptions>>()?.Value;
                var credential = entraOptions?.CustomCredential ?? new Azure.Identity.DefaultAzureCredential();
                graphClient = new GraphServiceClient(credential);
            }

            var options = sp.GetRequiredService<IOptions<GraphEmailDeliveryOptions>>();
            var logger = sp.GetService<ILogger<GraphEmailCredentialDeliveryService>>() ?? NullLogger<GraphEmailCredentialDeliveryService>.Instance;
            return new GraphEmailCredentialDeliveryService(graphClient, options, logger);
        });

        services.AddSingleton<ICredentialDeliveryService>(sp => sp.GetRequiredService<GraphEmailCredentialDeliveryService>());
        return services;
    }
}

