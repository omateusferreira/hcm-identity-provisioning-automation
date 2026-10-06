using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
builder.Services.AddGraphEmailCredentialDeliveryService(options => configuration.GetSection("GraphEmail").Bind(options));

builder.Build().Run();
