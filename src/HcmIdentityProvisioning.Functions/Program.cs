using Azure.Identity;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Graph;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

var configuration = builder.Configuration;

var syncSettings = new SyncSettings();
configuration.GetSection("HcmSync").Bind(syncSettings);

var rulesPath = configuration["RulesEngine:RulesFilePath"] ?? "Rules/rules.json";

builder.Services.AddHcmProvisioningCore(syncSettings, rulesPath);
builder.Services.AddEntraIdGraphAdapter(options => configuration.GetSection("EntraId").Bind(options));
builder.Services.AddGenericRestHcmConnector(options => configuration.GetSection("HcmRest").Bind(options));

builder.Services.AddSingleton<GraphServiceClient>(sp =>
{
    var entraOptions = sp.GetRequiredService<IOptions<EntraIdGraphOptions>>().Value;
    var credential = entraOptions.CustomCredential ?? new DefaultAzureCredential();
    return new GraphServiceClient(credential);
});

builder.Services.AddGraphEmailCredentialDeliveryService(options => configuration.GetSection("GraphEmail").Bind(options));

builder.Build().Run();
