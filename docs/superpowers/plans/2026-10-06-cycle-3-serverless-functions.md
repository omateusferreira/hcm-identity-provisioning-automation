# Implementation Plan: Ciclo 3 — Automação Serverless & Observabilidade

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar o host serverless Azure Functions (.NET 10 Isolated Worker), o gatilho agendado periódico `SyncTimerFunction`, a entrega out-of-band de credenciais via Microsoft Graph Mail API com shared mailbox (`no-reply`) e observabilidade semântica no Azure Application Insights.

**Architecture:** Clean Architecture / Ports & Adapters. `GraphEmailCredentialDeliveryService` implementa `ICredentialDeliveryService` em `Infrastructure`. O projeto `HcmIdentityProvisioning.Functions` atua puramente como Composition Root e agendador serverless, orquestrando `ReconcileBatchUseCase` a cada ciclo e registrando métricas em `customDimensions`.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Microsoft.Azure.Functions.Worker 2.x, Microsoft.Azure.Functions.Worker.Extensions.Timer 4.x, Microsoft.Azure.Functions.Worker.ApplicationInsights 2.x, Microsoft.Graph SDK v5, xUnit, FluentAssertions, NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-10-06-cycle-3-serverless-functions-design.md`](file:///C:/Users/mateu/.gemini/antigravity/worktrees/hcm-identity-provisioning-automation/plan_cycle_three_implementation/docs/superpowers/specs/2026-10-06-cycle-3-serverless-functions-design.md)

## Global Constraints

- Runtime: .NET 10 (`net10.0`), C# 14 com `<Nullable>enable</Nullable>` e `<ImplicitUsings>enable</ImplicitUsings>`.
- Isolated Worker Model: `Microsoft.Azure.Functions.Worker` em processo isolado.
- Extensibilidade de Credenciais: Extrair o destinatário estritamente da chave `"email"` (insensível a maiúsculas/minúsculas) de `employee.ExtendedAttributes`.
- Menor Privilégio & Shared Mailbox: Envio de e-mail através da caixa de correio compartilhada configurada em `SenderEmail` via `POST /users/{sender}/sendMail`.
- Zero Password Logging: Senhas temporárias **jamais** devem ser gravadas em logs ou telemetria.
- Trigger Scope: Apenas `TimerTrigger` com cron externalizado via `%SyncSchedule%`. Sem webhooks ou rotas HTTP públicas.
- Resiliência & Circuit Breaker: Registrar log de severidade `Critical` (`CRITICAL_AUDIT_BREAKER_TRIPPED`) quando `report.CircuitBreakerTripped == true`.

---

### Task 1: Serviço de Entrega de Credenciais via Graph Mail (`GraphEmailCredentialDeliveryService`)

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/Notifications/GraphEmailDeliveryOptions.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/Notifications/GraphEmailCredentialDeliveryService.cs`
- Modify: `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/Notifications/GraphEmailCredentialDeliveryServiceTests.cs`

**Interfaces:**
- Consumes: `HcmIdentityProvisioning.Domain.Ports.ICredentialDeliveryService`, `Microsoft.Graph.GraphServiceClient`, `Microsoft.Extensions.Options.IOptions<GraphEmailDeliveryOptions>`
- Produces: `AddGraphEmailCredentialDeliveryService(Action<GraphEmailDeliveryOptions>? configureOptions = null)` em `ServiceCollectionExtensions`

- [ ] **Step 1: Escrever testes unitários que falham para `GraphEmailCredentialDeliveryService`**

Criar `tests/HcmIdentityProvisioning.Infrastructure.Tests/Notifications/GraphEmailCredentialDeliveryServiceTests.cs`:
```csharp
using System.Net;
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Notifications;

public class GraphEmailCredentialDeliveryServiceTests
{
    private static GraphServiceClient CreateMockGraphClient(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        return new GraphServiceClient(httpClient, new AnonymousAuthenticationProvider());
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenSenderEmailEmpty_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler();
        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions { SenderEmail = "" });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP01").Value,
            "John Doe",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string> { ["email"] = "john.doe@external.com" });

        var upn = UserPrincipalName.Create("john.doe@company.onmicrosoft.com").Value;

        var act = () => service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SenderEmail*");
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmailAttributeMissing_SkipsWithoutCallingGraph()
    {
        var handler = new MockHttpMessageHandler();
        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions { SenderEmail = "no-reply@company.com" });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP01").Value,
            "John Doe",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string>()); // sem chave "email"

        var upn = UserPrincipalName.Create("john.doe@company.onmicrosoft.com").Value;

        await service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");

        handler.SentRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmailPresent_SendsMailViaGraphApi()
    {
        var handler = new MockHttpMessageHandler();
        handler.RegisterResponse("POST", "users/no-reply@company.com/sendMail", HttpStatusCode.Accepted, "{}");

        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions
        {
            SenderEmail = "no-reply@company.com",
            Subject = "Bem-vindo",
            SaveToSentItems = false
        });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP01").Value,
            "John Doe",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string> { ["email"] = "john.recipient@external.com" });

        var upn = UserPrincipalName.Create("john.doe@company.onmicrosoft.com").Value;

        await service.DeliverInitialCredentialsAsync(employee, upn, "SecretTempPass123!");

        handler.SentRequests.Should().HaveCount(1);
        var request = handler.SentRequests[0];
        request.RequestUri!.ToString().Should().Contain("users/no-reply@company.com/sendMail");
        request.Content.Should().NotBeNull();
        var content = await request.Content!.ReadAsStringAsync();
        content.Should().Contain("john.recipient@external.com");
        content.Should().Contain("john.doe@company.onmicrosoft.com");
        content.Should().Contain("SecretTempPass123!");
    }
}
```

- [ ] **Step 2: Executar testes para confirmar falha de compilação**

Executar: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter GraphEmailCredentialDeliveryServiceTests`
Esperado: Falha de compilação (tipos `GraphEmailDeliveryOptions` e `GraphEmailCredentialDeliveryService` ainda não existem).

- [ ] **Step 3: Implementar `GraphEmailDeliveryOptions` e `GraphEmailCredentialDeliveryService`**

Criar `src/HcmIdentityProvisioning.Infrastructure/Notifications/GraphEmailDeliveryOptions.cs`:
```csharp
namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class GraphEmailDeliveryOptions
{
    public string SenderEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = "Suas credenciais de primeiro acesso - Bem-vindo(a)!";
    public bool SaveToSentItems { get; set; } = false;
}
```

Criar `src/HcmIdentityProvisioning.Infrastructure/Notifications/GraphEmailCredentialDeliveryService.cs`:
```csharp
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class GraphEmailCredentialDeliveryService : ICredentialDeliveryService
{
    private readonly GraphServiceClient _graphClient;
    private readonly GraphEmailDeliveryOptions _options;
    private readonly ILogger<GraphEmailCredentialDeliveryService> _logger;

    public GraphEmailCredentialDeliveryService(
        GraphServiceClient graphClient,
        IOptions<GraphEmailDeliveryOptions> options,
        ILogger<GraphEmailCredentialDeliveryService> logger)
    {
        _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DeliverInitialCredentialsAsync(
        Employee employee,
        UserPrincipalName upn,
        string temporaryPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.SenderEmail))
        {
            throw new InvalidOperationException("SenderEmail must be configured in GraphEmailDeliveryOptions.");
        }

        if (!TryGetDeliveryEmail(employee, out var recipientEmail))
        {
            _logger.LogWarning("CREDENTIAL_DELIVERY_SKIPPED: Employee {EmployeeId} has no out-of-band email configured", employee.Id);
            return;
        }

        try
        {
            var requestBody = new SendMailPostRequestBody
            {
                Message = new Message
                {
                    Subject = _options.Subject,
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient
                        {
                            EmailAddress = new EmailAddress
                            {
                                Address = recipientEmail
                            }
                        }
                    },
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = $@"
                            <p>Olá <strong>{employee.FullName}</strong>,</p>
                            <p>Sua conta corporativa foi provisionada no Microsoft Entra ID:</p>
                            <ul>
                                <li><strong>Usuário (UPN):</strong> {upn.Value}</li>
                                <li><strong>Senha Temporária:</strong> {temporaryPassword}</li>
                            </ul>
                            <p>No primeiro login, você deverá obrigatoriamente redefinir esta senha.</p>
                            <p>Acesse o portal: <a href=""https://myapplications.microsoft.com"">https://myapplications.microsoft.com</a></p>"
                    }
                },
                SaveToSentItems = _options.SaveToSentItems
            };

            await _graphClient.Users[_options.SenderEmail]
                .SendMail
                .PostAsync(requestBody, cancellationToken: ct);

            _logger.LogInformation(
                "CREDENTIAL_DELIVERY_SUCCESS: Dispatched initial credentials for employee {EmployeeId} ({Upn}) to {RecipientEmail}",
                employee.Id, upn, recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "CREDENTIAL_DELIVERY_FAILED: Error delivering credentials for employee {EmployeeId} to {RecipientEmail}",
                employee.Id, recipientEmail);
        }
    }

    private static bool TryGetDeliveryEmail(Employee employee, out string email)
    {
        if (employee.ExtendedAttributes != null)
        {
            foreach (var kvp in employee.ExtendedAttributes)
            {
                if (string.Equals(kvp.Key, "email", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    email = kvp.Value.Trim();
                    return true;
                }
            }
        }

        email = string.Empty;
        return false;
    }
}
```

Atualizar `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`:
Adicionar método de extensão:
```csharp
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

        services.AddSingleton<ICredentialDeliveryService, GraphEmailCredentialDeliveryService>();
        return services;
    }
```

- [ ] **Step 4: Executar testes para verificar se passam**

Executar: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter GraphEmailCredentialDeliveryServiceTests`
Esperado: 3 passed, 0 failed.

- [ ] **Step 5: Fazer commit das alterações da Task 1**

```bash
git add src/HcmIdentityProvisioning.Infrastructure tests/HcmIdentityProvisioning.Infrastructure.Tests
git commit -m "feat(infrastructure): implement GraphEmailCredentialDeliveryService using Graph Mail API"
```

---

### Task 2: Criação e Configuração do Projeto Azure Functions (`HcmIdentityProvisioning.Functions`)

**Files:**
- Create: `src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj`
- Create: `src/HcmIdentityProvisioning.Functions/host.json`
- Create: `src/HcmIdentityProvisioning.Functions/local.settings.json`
- Modify: `HcmIdentityProvisioning.sln`

**Interfaces:**
- Consumes: `net10.0`, `Microsoft.Azure.Functions.Worker`
- Produces: Assembly executável do host serverless

- [ ] **Step 1: Criar o arquivo de projeto `HcmIdentityProvisioning.Functions.csproj`**

Criar `src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.0.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Timer" Version="4.3.1" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="2.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\HcmIdentityProvisioning.Application\HcmIdentityProvisioning.Application.csproj" />
    <ProjectReference Include="..\HcmIdentityProvisioning.Infrastructure\HcmIdentityProvisioning.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Update="local.settings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>Never</CopyToPublishDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Criar os arquivos `host.json` e `local.settings.json`**

Criar `src/HcmIdentityProvisioning.Functions/host.json`:
```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "excludedTypes": "Request"
      },
      "enableLiveMetricsFilters": true
    },
    "logLevel": {
      "default": "Information",
      "HcmIdentityProvisioning": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

Criar `src/HcmIdentityProvisioning.Functions/local.settings.json`:
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SyncSchedule": "0 */30 * * * *",
    "HcmSync__TenantDomain": "company.onmicrosoft.com",
    "HcmSync__ManagedGroupPrefix": "grp-iam-",
    "HcmSync__BatchPageSize": "50",
    "HcmSync__MaxDisablementPercentage": "10.0",
    "HcmSync__MaxDisablementCount": "25",
    "GraphEmail__SenderEmail": "no-reply@company.com"
  }
}
```

- [ ] **Step 3: Adicionar projeto à solução `HcmIdentityProvisioning.sln`**

Executar: `dotnet sln HcmIdentityProvisioning.sln add src/HcmIdentityProvisioning.Functions/HcmIdentityProvisioning.Functions.csproj`

- [ ] **Step 4: Compilar projeto Functions para verificar resolução de pacotes e referências**

Executar: `dotnet build src/HcmIdentityProvisioning.Functions`
Esperado: Compilação com êxito.

- [ ] **Step 5: Fazer commit do scaffolding do projeto Functions**

```bash
git add src/HcmIdentityProvisioning.Functions HcmIdentityProvisioning.sln
git commit -m "feat(functions): scaffold HcmIdentityProvisioning.Functions project"
```

---

### Task 3: Implementação de `SyncTimerFunction` e Composition Root (`Program.cs`)

**Files:**
- Create: `src/HcmIdentityProvisioning.Functions/Functions/SyncTimerFunction.cs`
- Create: `src/HcmIdentityProvisioning.Functions/Program.cs`

**Interfaces:**
- Consumes: `ReconcileBatchUseCase`, `SyncSettings`, `EntraIdGraphAdapter`, `GenericRestHcmConnector`, `GraphEmailCredentialDeliveryService`
- Produces: Função serverless executável agendada por CRON

- [ ] **Step 1: Implementar `SyncTimerFunction.cs`**

Criar `src/HcmIdentityProvisioning.Functions/Functions/SyncTimerFunction.cs`:
```csharp
using HcmIdentityProvisioning.Application.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace HcmIdentityProvisioning.Functions.Functions;

public sealed class SyncTimerFunction
{
    private readonly ReconcileBatchUseCase _useCase;
    private readonly ILogger<SyncTimerFunction> _logger;

    public SyncTimerFunction(
        ReconcileBatchUseCase useCase,
        ILogger<SyncTimerFunction> logger)
    {
        _useCase = useCase ?? throw new ArgumentNullException(nameof(useCase));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Function(nameof(SyncTimerFunction))]
    public async Task Run(
        [TimerTrigger("%SyncSchedule%")] TimerInfo timerInfo,
        FunctionContext context)
    {
        _logger.LogInformation("HCM Identity Sync cycle started at {Time}", DateTimeOffset.UtcNow);

        try
        {
            var report = await _useCase.ExecuteAsync(context.CancellationToken);

            _logger.LogInformation(
                "HCM Identity Sync completed. Total: {Total}, Created: {Created}, Updated: {Updated}, " +
                "Enabled: {Enabled}, Disabled: {Disabled}, Revoked: {Revoked}, GroupsAdded: {GrpAdd}, GroupsRemoved: {GrpRem}",
                report.TotalProcessed, report.CreatedCount, report.UpdatedCount,
                report.EnabledCount, report.DisabledCount, report.SessionsRevokedCount,
                report.GroupMembershipsAdded, report.GroupMembershipsRemoved);

            if (report.CircuitBreakerTripped)
            {
                _logger.LogCritical("CRITICAL_AUDIT_BREAKER_TRIPPED: {Reason}", report.CircuitBreakerMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing HCM Identity Sync cycle");
            throw;
        }
    }
}
```

- [ ] **Step 2: Implementar o Composition Root em `Program.cs`**

Criar `src/HcmIdentityProvisioning.Functions/Program.cs`:
```csharp
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
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
```

- [ ] **Step 3: Compilar o projeto `HcmIdentityProvisioning.Functions`**

Executar: `dotnet build src/HcmIdentityProvisioning.Functions`
Esperado: Compilação concluída com sucesso (0 warnings, 0 errors).

- [ ] **Step 4: Fazer commit da `SyncTimerFunction` e do `Program.cs`**

```bash
git add src/HcmIdentityProvisioning.Functions
git commit -m "feat(functions): implement SyncTimerFunction and production composition root"
```

---

### Task 4: Suíte de Testes Automatizados para Azure Functions (`HcmIdentityProvisioning.Functions.Tests`)

**Files:**
- Create: `tests/HcmIdentityProvisioning.Functions.Tests/HcmIdentityProvisioning.Functions.Tests.csproj`
- Create: `tests/HcmIdentityProvisioning.Functions.Tests/Functions/SyncTimerFunctionTests.cs`
- Create: `tests/HcmIdentityProvisioning.Functions.Tests/DependencyInjection/HostCompositionTests.cs`
- Modify: `HcmIdentityProvisioning.sln`

**Interfaces:**
- Consumes: `SyncTimerFunction`, `ReconcileBatchUseCase`, `SyncReport`
- Produces: Testes unitários do timer e validação de resolução de DI

- [ ] **Step 1: Criar o arquivo de projeto `HcmIdentityProvisioning.Functions.Tests.csproj`**

Criar `tests/HcmIdentityProvisioning.Functions.Tests/HcmIdentityProvisioning.Functions.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" Version="8.0.1" />
    <PackageReference Include="NSubstitute" Version="5.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\HcmIdentityProvisioning.Functions\HcmIdentityProvisioning.Functions.csproj" />
  </ItemGroup>
</Project>
```

Adicionar à solução:
`dotnet sln HcmIdentityProvisioning.sln add tests/HcmIdentityProvisioning.Functions.Tests/HcmIdentityProvisioning.Functions.Tests.csproj`

- [ ] **Step 2: Escrever testes unitários em `SyncTimerFunctionTests.cs`**

Criar `tests/HcmIdentityProvisioning.Functions.Tests/Functions/SyncTimerFunctionTests.cs`:
```csharp
using FluentAssertions;
using HcmIdentityProvisioning.Application.Models;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Functions.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Functions.Tests.Functions;

public class SyncTimerFunctionTests
{
    private readonly IHcmConnector _connector = Substitute.For<IHcmConnector>();
    private readonly IIdentityStore _identityStore = Substitute.For<IIdentityStore>();
    private readonly IRulesEngine _rulesEngine = Substitute.For<IRulesEngine>();
    private readonly ICircuitBreaker _circuitBreaker = Substitute.For<ICircuitBreaker>();
    private readonly ICredentialDeliveryService _credentials = Substitute.For<ICredentialDeliveryService>();
    private readonly ILogger<SyncTimerFunction> _logger = Substitute.For<ILogger<SyncTimerFunction>>();
    private readonly FunctionContext _context = Substitute.For<FunctionContext>();

    private ReconcileBatchUseCase CreateUseCase()
    {
        var reconciler = new IdentityReconciliationService(_rulesEngine);
        var settings = new SyncSettings { TenantDomain = "company.onmicrosoft.com" };
        return new ReconcileBatchUseCase(
            _connector,
            _identityStore,
            reconciler,
            _circuitBreaker,
            _credentials,
            settings,
            NullLogger<ReconcileBatchUseCase>.Instance);
    }

    [Fact]
    public async Task Run_WhenExecuted_CallsReconcileBatchUseCaseSuccessfully()
    {
        _connector.GetEmployeesPageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<HcmIdentityProvisioning.Domain.Entities.Employee>(new List<HcmIdentityProvisioning.Domain.Entities.Employee>(), 1, 50, false));

        var useCase = CreateUseCase();
        var function = new SyncTimerFunction(useCase, _logger);
        var timerInfo = new TimerInfo();

        var act = () => function.Run(timerInfo, _context);
        await act.Should().NotThrowAsync();

        await _connector.Received(1).GetEmployeesPageAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_WhenCircuitBreakerTrips_LogsCriticalAlert()
    {
        var employee = new HcmIdentityProvisioning.Domain.Entities.Employee(
            HcmIdentityProvisioning.Domain.ValueObjects.EmployeeId.Create("EMP1").Value,
            "Jane Doe",
            HcmIdentityProvisioning.Domain.Enums.EmployeeStatus.Inactive,
            "HR",
            "Specialist",
            new Dictionary<string, string>());

        _connector.GetEmployeesPageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<HcmIdentityProvisioning.Domain.Entities.Employee>(new List<HcmIdentityProvisioning.Domain.Entities.Employee> { employee }, 1, 50, false));

        var user = new HcmIdentityProvisioning.Domain.Entities.EntraUser(
            Guid.NewGuid(),
            employee.Id,
            HcmIdentityProvisioning.Domain.ValueObjects.UserPrincipalName.Create("jane.doe@company.onmicrosoft.com").Value,
            "Jane Doe",
            true,
            new HashSet<Guid>());

        _identityStore.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<HcmIdentityProvisioning.Domain.ValueObjects.EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<HcmIdentityProvisioning.Domain.ValueObjects.EmployeeId, HcmIdentityProvisioning.Domain.Entities.EntraUser> { [employee.Id] = user });

        _circuitBreaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<HcmIdentityProvisioning.Domain.Actions.DeltaAction>>(), out Arg.Any<string>())
            .Returns(x =>
            {
                x[2] = "Disablement volume exceeded safe threshold";
                return true;
            });

        var useCase = CreateUseCase();
        var function = new SyncTimerFunction(useCase, _logger);
        var timerInfo = new TimerInfo();

        await function.Run(timerInfo, _context);

        _logger.Received().Log(
            LogLevel.Critical,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("CRITICAL_AUDIT_BREAKER_TRIPPED")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Run_WhenUseCaseThrows_LogsErrorAndReThrows()
    {
        _connector.GetEmployeesPageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<PagedResult<HcmIdentityProvisioning.Domain.Entities.Employee>>(_ => throw new HttpRequestException("Network failure"));

        var useCase = CreateUseCase();
        var function = new SyncTimerFunction(useCase, _logger);
        var timerInfo = new TimerInfo();

        var act = () => function.Run(timerInfo, _context);
        await act.Should().ThrowAsync<HttpRequestException>();

        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<HttpRequestException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
```

- [ ] **Step 3: Escrever teste de composição em `HostCompositionTests.cs`**

Criar `tests/HcmIdentityProvisioning.Functions.Tests/DependencyInjection/HostCompositionTests.cs`:
```csharp
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Functions.Functions;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HcmIdentityProvisioning.Functions.Tests.DependencyInjection;

public class HostCompositionTests
{
    [Fact]
    public void BuildServiceProvider_CanResolveSyncTimerFunctionAndUseCases()
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var syncSettings = new SyncSettings
        {
            TenantDomain = "company.onmicrosoft.com",
            ManagedGroupPrefix = "grp-iam-"
        };

        var rulesJsonPath = Path.Combine(AppContext.BaseDirectory, "Rules", "rules.json");
        if (!File.Exists(rulesJsonPath))
        {
            var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HcmIdentityProvisioning.Infrastructure", "Rules", "rules.json"));
            rulesJsonPath = File.Exists(fallback) ? fallback : rulesJsonPath;
        }

        services.AddHcmProvisioningCore(syncSettings, rulesJsonPath);
        services.AddSyntheticHcmConnector(Path.Combine(AppContext.BaseDirectory, "dummy-fixtures.json"));
        services.AddInMemoryIdentityStore();
        services.AddTransient<SyncTimerFunction>();

        using var provider = services.BuildServiceProvider();
        var func = provider.GetService<SyncTimerFunction>();

        func.Should().NotBeNull();
        provider.GetService<ReconcileBatchUseCase>().Should().NotBeNull();
    }
}
```

- [ ] **Step 4: Executar testes de Functions**

Executar: `dotnet test tests/HcmIdentityProvisioning.Functions.Tests`
Esperado: 4 passed, 0 failed.

- [ ] **Step 5: Fazer commit dos testes de Functions**

```bash
git add tests/HcmIdentityProvisioning.Functions.Tests HcmIdentityProvisioning.sln
git commit -m "test(functions): add SyncTimerFunction unit tests and DI host composition tests"
```

---

### Task 5: Validação de Regressão Global & Atualização do Roadmap

**Files:**
- Modify: `docs/superpowers/execution-roadmap.md`

- [ ] **Step 1: Executar toda a suíte de testes da solução**

Executar: `dotnet test`
Esperado:
- `Domain.Tests`: 42 aprovados
- `Application.Tests`: 27 aprovados
- `Infrastructure.Tests`: 98 aprovados (95 existentes + 3 novos)
- `Functions.Tests`: 4 aprovados
- Total: 171 testes aprovados, 0 falhas.

- [ ] **Step 2: Atualizar `docs/superpowers/execution-roadmap.md`**

Marcar o Ciclo 3 como concluído com a contagem total de testes (171 testes) e link para a spec do Ciclo 3.

- [ ] **Step 3: Fazer commit final do Ciclo 3**

```bash
git add docs/superpowers/execution-roadmap.md
git commit -m "docs: complete cycle 3 in execution roadmap"
```
