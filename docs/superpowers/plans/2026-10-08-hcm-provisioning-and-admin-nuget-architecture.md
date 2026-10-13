# Plano de Implementação: Arquitetura de 2 Pacotes NuGet (HcmIdentityProvisioning & HcmIdentityProvisioning.Admin) e Extensibilidade via DI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reestruturar a distribuição de pacotes NuGet em exatamente 2 pacotes públicos de alto nível orientados à intenção de consumo (`HcmIdentityProvisioning` para serviços/Azure Functions e `HcmIdentityProvisioning.Admin` para CLIs e ferramentas de gestão), isolando as camadas internas (`Domain`, `Application` com `IsPackable=false`) e fornecendo APIs fluentes de extensibilidade via DI para conectores proprietários e comandos de terminal.

**Architecture:** O motor de provisionamento operacional é consolidado no pacote público `HcmIdentityProvisioning` com um builder fluente `IHcmProvisioningBuilder` em DI. O SDK de console e ferramentas administrativas é isolado no pacote `HcmIdentityProvisioning.Admin` com `HcmAdminCliBuilder` e descriptors `IHcmConnectorCliDescriptor`. O projeto `HcmIdentityProvisioning.Cli` torna-se a ferramenta de console de referência e demonstração (*dogfooding*).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Microsoft.Extensions.DependencyInjection, System.CommandLine, xUnit, FluentAssertions, NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-10-08-hcm-provisioning-and-admin-nuget-architecture-design.md`](../specs/2026-10-08-hcm-provisioning-and-admin-nuget-architecture-design.md)

## Global Constraints

- Runtime: .NET 10 (`net10.0`), C# 14 com `Nullable: enable` e `ImplicitUsings: enable`.
- Pacotes Públicos NuGet: Exatamente 2 pacotes na versão `1.1.0` com licença MIT: `HcmIdentityProvisioning` e `HcmIdentityProvisioning.Admin`.
- Camadas Internas: `HcmIdentityProvisioning.Domain` e `HcmIdentityProvisioning.Application` marcados com `<IsPackable>false</IsPackable>`.
- Executáveis: `HcmIdentityProvisioning.Cli` e `HcmIdentityProvisioning.Functions` marcados com `<IsPackable>false</IsPackable>`.
- Zero Password Exposure: Nenhuma senha ou credencial em logs, telemetria ou tela não mascarada.
- Não-Regressão: 100% de aprovação em todos os testes unitários e de integração existentes.

---

### Task 1: Fluent Core Builder (`IHcmProvisioningBuilder` e `HcmProvisioningBuilder`) no Core

**Files:**
- Create: `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/IHcmProvisioningBuilder.cs`
- Create: `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/HcmProvisioningBuilder.cs`
- Modify: `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/HcmIdentityProvisioning.Infrastructure.Tests/DependencyInjection/HcmProvisioningBuilderTests.cs`

**Interfaces:**
- Produces: `IHcmProvisioningBuilder`, `HcmProvisioningBuilder`, `services.AddHcmProvisioning(settings, rulesPath)`.
- Consumes: `SyncSettings`, `IHcmConnector`, `IIdentityStore`, `ICredentialDeliveryService`.

- [ ] **Step 1: Escrever teste de unidade para o Fluent Core Builder**

Em `tests/HcmIdentityProvisioning.Infrastructure.Tests/DependencyInjection/HcmProvisioningBuilderTests.cs`:
```csharp
using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.DependencyInjection;

public class HcmProvisioningBuilderTests
{
    private class TestCustomConnector : IHcmConnector
    {
        public Task<PagedResult<Employee>> GetEmployeesPageAsync(int pageNumber, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<Employee>(Array.Empty<Employee>(), pageNumber, pageSize, 0, false));
    }

    private class TestOptions
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    private class TestConfigurableConnector : IHcmConnector
    {
        public TestOptions Options { get; }
        public TestConfigurableConnector(IOptions<TestOptions> options) => Options = options.Value;

        public Task<PagedResult<Employee>> GetEmployeesPageAsync(int pageNumber, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<Employee>(Array.Empty<Employee>(), pageNumber, pageSize, 0, false));
    }

    [Fact]
    public void AddHcmProvisioning_RegistersCoreServicesAndReturnsBuilder()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        var builder = services.AddHcmProvisioning(settings, "Rules/rules.json");

        builder.Should().NotBeNull();
        builder.Services.Should().BeSameAs(services);
        builder.Settings.Should().BeSameAs(settings);
    }

    [Fact]
    public void AddConnector_Generic_RegistersConnectorAndResolvesUseCases()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddInMemoryStore()
            .AddConnector<TestCustomConnector>();

        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>();
        connector.Should().BeOfType<TestCustomConnector>();

        var useCase = sp.GetRequiredService<ReconcileBatchUseCase>();
        useCase.Should().NotBeNull();
    }

    [Fact]
    public void AddConnector_WithOptions_BindsOptionsCorrectly()
    {
        var services = new ServiceCollection();
        var settings = new SyncSettings { TenantDomain = "contoso.com" };

        services.AddHcmProvisioning(settings, "Rules/rules.json")
            .AddInMemoryStore()
            .AddConnector<TestConfigurableConnector, TestOptions>(opts =>
            {
                opts.ApiKey = "secret-token-123";
            });

        var sp = services.BuildServiceProvider();
        var connector = sp.GetRequiredService<IHcmConnector>() as TestConfigurableConnector;
        connector.Should().NotBeNull();
        connector!.Options.ApiKey.Should().Be("secret-token-123");
    }
}
```

- [ ] **Step 2: Executar teste para verificar que falha**

Execute: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests --filter "HcmProvisioningBuilderTests"`
Esperado: FAIL (compilation error: `IHcmProvisioningBuilder` / `AddHcmProvisioning` não encontrados).

- [ ] **Step 3: Implementar `IHcmProvisioningBuilder` e `HcmProvisioningBuilder`**

Criar `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/IHcmProvisioningBuilder.cs`:
```csharp
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public interface IHcmProvisioningBuilder
{
    IServiceCollection Services { get; }
    SyncSettings Settings { get; }

    IHcmProvisioningBuilder AddConnector<TConnector>()
        where TConnector : class, IHcmConnector;

    IHcmProvisioningBuilder AddConnector<TConnector, TOptions>(Action<TOptions> configureOptions)
        where TConnector : class, IHcmConnector
        where TOptions : class;

    IHcmProvisioningBuilder AddSyntheticConnector(string fixturesPath);

    IHcmProvisioningBuilder AddGenericRestConnector(Action<RestHcmConnectorOptions>? configureOptions = null);

    IHcmProvisioningBuilder AddEntraIdStore(Action<EntraIdGraphOptions>? configureOptions = null);

    IHcmProvisioningBuilder AddInMemoryStore(Action<InMemoryIdentityStore>? seedAction = null);

    IHcmProvisioningBuilder AddCredentialDelivery<TDelivery>()
        where TDelivery : class, ICredentialDeliveryService;

    IHcmProvisioningBuilder AddGraphEmailCredentialDelivery(Action<GraphEmailOptions> configureOptions);
}
```

Criar `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/HcmProvisioningBuilder.cs`:
```csharp
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.Graph;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Infrastructure.DependencyInjection;

public sealed class HcmProvisioningBuilder : IHcmProvisioningBuilder
{
    public IServiceCollection Services { get; }
    public SyncSettings Settings { get; }

    public HcmProvisioningBuilder(IServiceCollection services, SyncSettings settings)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IHcmProvisioningBuilder AddConnector<TConnector>()
        where TConnector : class, IHcmConnector
    {
        Services.AddSingleton<IHcmConnector, TConnector>();
        return this;
    }

    public IHcmProvisioningBuilder AddConnector<TConnector, TOptions>(Action<TOptions> configureOptions)
        where TConnector : class, IHcmConnector
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        Services.Configure(configureOptions);
        Services.AddSingleton<IHcmConnector, TConnector>();
        return this;
    }

    public IHcmProvisioningBuilder AddSyntheticConnector(string fixturesPath)
    {
        Services.AddSyntheticHcmConnector(fixturesPath);
        return this;
    }

    public IHcmProvisioningBuilder AddGenericRestConnector(Action<RestHcmConnectorOptions>? configureOptions = null)
    {
        Services.AddGenericRestHcmConnector(configureOptions);
        return this;
    }

    public IHcmProvisioningBuilder AddEntraIdStore(Action<EntraIdGraphOptions>? configureOptions = null)
    {
        Services.AddEntraIdGraphAdapter(configureOptions);
        return this;
    }

    public IHcmProvisioningBuilder AddInMemoryStore(Action<InMemoryIdentityStore>? seedAction = null)
    {
        Services.AddInMemoryIdentityStore(seedAction);
        return this;
    }

    public IHcmProvisioningBuilder AddCredentialDelivery<TDelivery>()
        where TDelivery : class, ICredentialDeliveryService
    {
        Services.AddSingleton<ICredentialDeliveryService, TDelivery>();
        return this;
    }

    public IHcmProvisioningBuilder AddGraphEmailCredentialDelivery(Action<GraphEmailOptions> configureOptions)
    {
        Services.AddGraphEmailCredentialDeliveryService(configureOptions);
        return this;
    }
}
```

Em `src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, adicionar o método de extensão:
```csharp
    public static IHcmProvisioningBuilder AddHcmProvisioning(
        this IServiceCollection services,
        SyncSettings settings,
        string rulesJsonPath)
    {
        services.AddHcmProvisioningCore(settings, rulesJsonPath);
        return new HcmProvisioningBuilder(services, settings);
    }
```

- [ ] **Step 4: Executar testes de infraestrutura para verificar aprovação**

Execute: `dotnet test tests/HcmIdentityProvisioning.Infrastructure.Tests`
Esperado: PASS (todos os testes passando, incluindo `HcmProvisioningBuilderTests`).

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/IHcmProvisioningBuilder.cs src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/HcmProvisioningBuilder.cs src/HcmIdentityProvisioning.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/HcmIdentityProvisioning.Infrastructure.Tests/DependencyInjection/HcmProvisioningBuilderTests.cs
git commit -m "feat(infrastructure): implement IHcmProvisioningBuilder fluent DI API"
```

---

### Task 2: Criação da Biblioteca `HcmIdentityProvisioning.Admin` (CLI SDK, Descriptors e Builder)

**Files:**
- Create: `src/HcmIdentityProvisioning.Admin/HcmIdentityProvisioning.Admin.csproj`
- Create: `src/HcmIdentityProvisioning.Admin/Extensibility/IHcmConnectorCliDescriptor.cs`
- Create: `src/HcmIdentityProvisioning.Admin/HcmAdminCliBuilder.cs`
- Create: `src/HcmIdentityProvisioning.Admin/Descriptors/SyntheticHcmConnectorCliDescriptor.cs`
- Create: `src/HcmIdentityProvisioning.Admin/Descriptors/GenericRestHcmConnectorCliDescriptor.cs`
- Move/Reutilizar de `Cli`: `Commands/SyncCommand.cs`, `Commands/ValidateRulesCommand.cs`, `Commands/EnsureGroupsCommand.cs`, `Commands/SyncCliOptions.cs`, `Utils/IConsolePrompter.cs`, `Utils/ConsolePrompter.cs`, `Utils/PathResolver.cs`
- Modify: `HcmIdentityProvisioning.sln`
- Create: `tests/HcmIdentityProvisioning.Admin.Tests/HcmIdentityProvisioning.Admin.Tests.csproj`
- Create: `tests/HcmIdentityProvisioning.Admin.Tests/HcmAdminCliBuilderTests.cs`

**Interfaces:**
- Produces: `HcmAdminCliBuilder`, `IHcmConnectorCliDescriptor`, comandos administrativos reutilizáveis em `HcmIdentityProvisioning.Admin`.
- Consumes: `HcmIdentityProvisioning.Infrastructure`, `System.CommandLine`.

- [ ] **Step 1: Criar projeto `HcmIdentityProvisioning.Admin.csproj` e adicionar à solution**

Criar `src/HcmIdentityProvisioning.Admin/HcmIdentityProvisioning.Admin.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackageId>HcmIdentityProvisioning.Admin</PackageId>
    <Version>1.1.0</Version>
    <Authors>Hcm Identity Provisioning Team</Authors>
    <Description>Extensible Administration CLI SDK and Tooling for HCM Identity Provisioning Engine</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryType>git</RepositoryType>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <IsPackable>true</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\HcmIdentityProvisioning.Infrastructure\HcmIdentityProvisioning.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.0" />
    <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
  </ItemGroup>

</Project>
```

Adicionar à solution:
```bash
dotnet sln HcmIdentityProvisioning.sln add src/HcmIdentityProvisioning.Admin/HcmIdentityProvisioning.Admin.csproj
```

- [ ] **Step 2: Mover utilitários e comandos reutilizáveis para `HcmIdentityProvisioning.Admin`**

Copiar/mover para `src/HcmIdentityProvisioning.Admin/`:
- `Utils/IConsolePrompter.cs` (namespace `HcmIdentityProvisioning.Admin.Utils`)
- `Utils/PathResolver.cs` (namespace `HcmIdentityProvisioning.Admin.Utils`)
- `Commands/SyncCliOptions.cs` (namespace `HcmIdentityProvisioning.Admin.Commands`)
- `Commands/SyncCommand.cs` (namespace `HcmIdentityProvisioning.Admin.Commands`)
- `Commands/EnsureGroupsCommand.cs` (namespace `HcmIdentityProvisioning.Admin.Commands`)
- `Commands/ValidateRulesCommand.cs` (namespace `HcmIdentityProvisioning.Admin.Commands`)

- [ ] **Step 3: Implementar `IHcmConnectorCliDescriptor` e Descriptors Nativos**

Criar `src/HcmIdentityProvisioning.Admin/Extensibility/IHcmConnectorCliDescriptor.cs`:
```csharp
using System.CommandLine;
using System.CommandLine.Parsing;
using HcmIdentityProvisioning.Admin.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Extensibility;

public interface IHcmConnectorCliDescriptor
{
    string ConnectorKey { get; }
    string Description { get; }
    IReadOnlyList<Option> GetCliOptions();
    void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions);
}
```

Criar `src/HcmIdentityProvisioning.Admin/Descriptors/SyntheticHcmConnectorCliDescriptor.cs`:
```csharp
using System.CommandLine;
using System.CommandLine.Parsing;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Descriptors;

public sealed class SyntheticHcmConnectorCliDescriptor : IHcmConnectorCliDescriptor
{
    public string ConnectorKey => "synthetic";
    public string Description => "Synthetic In-Memory HCM Connector (Fixture based)";

    private readonly Option<FileInfo?> _fixturesOption = new(
        name: "--fixtures",
        description: "Path to synthetic-employees.json file (defaults to application bundle or FIXTURES_FILE_PATH).");

    public IReadOnlyList<Option> GetCliOptions() => [_fixturesOption];

    public void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions)
    {
        var fixturesFile = parseResult.GetValueForOption(_fixturesOption);
        var fixturesPath = fixturesFile?.FullName ?? baseOptions.FixturesPath;
        var resolvedPath = PathResolver.ResolveFixturesPath(fixturesPath);
        services.AddSyntheticHcmConnector(resolvedPath);
    }
}
```

Criar `src/HcmIdentityProvisioning.Admin/Descriptors/GenericRestHcmConnectorCliDescriptor.cs`:
```csharp
using System.CommandLine;
using System.CommandLine.Parsing;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Infrastructure.Connectors.Rest;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HcmIdentityProvisioning.Admin.Descriptors;

public sealed class GenericRestHcmConnectorCliDescriptor : IHcmConnectorCliDescriptor
{
    public string ConnectorKey => "rest";
    public string Description => "Generic REST HCM Connector";

    private readonly Option<string?> _restBaseUrlOption = new(
        name: "--rest-url",
        description: "Base API URL for Generic REST HCM Connector.");

    private readonly Option<string?> _restAuthTokenOption = new(
        name: "--rest-token",
        description: "Bearer authentication token for Generic REST HCM Connector.");

    public IReadOnlyList<Option> GetCliOptions() => [_restBaseUrlOption, _restAuthTokenOption];

    public void ConfigureServices(IServiceCollection services, ParseResult parseResult, SyncCliOptions baseOptions)
    {
        var baseUrl = parseResult.GetValueForOption(_restBaseUrlOption);
        var token = parseResult.GetValueForOption(_restAuthTokenOption);

        services.AddGenericRestHcmConnector(opts =>
        {
            if (!string.IsNullOrWhiteSpace(baseUrl)) opts.BaseUrl = baseUrl;
            if (!string.IsNullOrWhiteSpace(token)) opts.AuthToken = token;
        });
    }
}
```

- [ ] **Step 4: Implementar `HcmAdminCliBuilder`**

Criar `src/HcmIdentityProvisioning.Admin/HcmAdminCliBuilder.cs`:
```csharp
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Azure.Identity;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Descriptors;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace HcmIdentityProvisioning.Admin;

public sealed class HcmAdminCliBuilder
{
    private readonly string[] _args;
    private readonly List<IHcmConnectorCliDescriptor> _descriptors = new();
    private readonly List<Action<IServiceCollection>> _serviceConfigurators = new();
    private readonly List<Action<RootCommand>> _rootCommandConfigurators = new();
    private readonly List<Command> _customCommands = new();
    private IConsolePrompter _prompter = new ConsolePrompter();

    private HcmAdminCliBuilder(string[] args)
    {
        _args = args ?? Array.Empty<string>();
    }

    public static HcmAdminCliBuilder Create(string[] args) => new(args);

    public HcmAdminCliBuilder WithPrompter(IConsolePrompter prompter)
    {
        _prompter = prompter ?? throw new ArgumentNullException(nameof(prompter));
        return this;
    }

    public HcmAdminCliBuilder AddConnector<TDescriptor>()
        where TDescriptor : IHcmConnectorCliDescriptor, new()
    {
        _descriptors.Add(new TDescriptor());
        return this;
    }

    public HcmAdminCliBuilder AddConnector(IHcmConnectorCliDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptors.Add(descriptor);
        return this;
    }

    public HcmAdminCliBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigurators.Add(configure);
        return this;
    }

    public HcmAdminCliBuilder ConfigureRootCommand(Action<RootCommand> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _rootCommandConfigurators.Add(configure);
        return this;
    }

    public HcmAdminCliBuilder AddCommand(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _customCommands.Add(command);
        return this;
    }

    public async Task<int> RunAsync()
    {
        var rootCommand = new RootCommand("HCM to Microsoft Entra ID Administration & Audit CLI");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose diagnostic logging (LogLevel.Information).");
        rootCommand.AddGlobalOption(verboseOption);

        var isVerbose = _args.Contains("--verbose", StringComparer.OrdinalIgnoreCase) ||
                        _args.Contains("-v", StringComparer.OrdinalIgnoreCase);

        // Subcomando sync com factory de DI dinâmica
        var syncCmd = SyncCommand.Create(
            opts => BuildServiceProvider(opts, isVerbose),
            _prompter,
            _descriptors);
        rootCommand.AddCommand(syncCmd);

        // Subcomando ensure-groups
        var ensureCmd = EnsureGroupsCommand.Create(
            opts => BuildServiceProvider(opts, isVerbose),
            _prompter);
        rootCommand.AddCommand(ensureCmd);

        // Subcomando validate-rules
        rootCommand.AddCommand(ValidateRulesCommand.Create());

        // Comandos customizados adicionais
        foreach (var cmd in _customCommands)
        {
            rootCommand.AddCommand(cmd);
        }

        foreach (var config in _rootCommandConfigurators)
        {
            config(rootCommand);
        }

        var exitCode = await rootCommand.InvokeAsync(_args);
        return Environment.ExitCode != 0 ? Environment.ExitCode : exitCode;
    }

    private IServiceProvider BuildServiceProvider(SyncCliOptions options, bool isVerbose)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole(cOptions =>
            {
                cOptions.LogToStandardErrorThreshold = LogLevel.Warning;
            }).SetMinimumLevel(isVerbose ? LogLevel.Information : LogLevel.Warning);
        });

        var settings = new SyncSettings
        {
            TenantDomain = options.TenantDomain,
            ManagedGroupPrefix = "grp-iam-"
        };

        var rulesPath = PathResolver.ResolveRulesPath(options.RulesPath);
        services.AddHcmProvisioningCore(settings, rulesPath);

        // Configuração de Identidades (Entra ID ou Memória)
        if (string.Equals(options.Idp, "entra", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Idp, "entra-id", StringComparison.OrdinalIgnoreCase))
        {
            services.AddEntraIdGraphAdapter(graphOpts =>
            {
                graphOpts.TenantDomain = options.TenantDomain;
            });
            services.AddSingleton<GraphServiceClient>(_ => new GraphServiceClient(new DefaultAzureCredential()));

            if (options.MockEmail || string.IsNullOrWhiteSpace(options.SenderEmail))
            {
                services.AddSingleton<ICredentialDeliveryService, MockCredentialDeliveryService>();
            }
            else
            {
                services.AddGraphEmailCredentialDeliveryService(mailOpts =>
                {
                    mailOpts.SenderEmail = options.SenderEmail;
                });
            }
        }
        else
        {
            services.AddInMemoryIdentityStore(store =>
            {
                store.SeedGroup(Guid.Parse("11111111-1111-1111-1111-111111111111"), "grp-iam-engineering");
                store.SeedGroup(Guid.Parse("22222222-2222-2222-2222-222222222222"), "grp-iam-finance");
                store.SeedGroup(Guid.Parse("33333333-3333-3333-3333-333333333333"), "grp-iam-all-staff");
            });
        }

        // Customizadores adicionais do consumidor
        foreach (var config in _serviceConfigurators)
        {
            config(services);
        }

        return services.BuildServiceProvider();
    }
}
```

- [ ] **Step 5: Criar testes para `HcmIdentityProvisioning.Admin.Tests`**

Criar `tests/HcmIdentityProvisioning.Admin.Tests/HcmIdentityProvisioning.Admin.Tests.csproj`:
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
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2" />
    <PackageReference Include="FluentAssertions" Version="8.1.1" />
    <PackageReference Include="NSubstitute" Version="5.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\HcmIdentityProvisioning.Admin\HcmIdentityProvisioning.Admin.csproj" />
  </ItemGroup>

</Project>
```
Adicionar à solution: `dotnet sln HcmIdentityProvisioning.sln add tests/HcmIdentityProvisioning.Admin.Tests/HcmIdentityProvisioning.Admin.Tests.csproj`

Criar `tests/HcmIdentityProvisioning.Admin.Tests/HcmAdminCliBuilderTests.cs`:
```csharp
using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Extensibility;
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

    [Fact]
    public async Task RunAsync_WhenHelpRequested_OutputsOptionsIncludingCustomDescriptorOptions()
    {
        using var sw = new StringWriter();
        Console.SetOut(sw);

        var builder = HcmAdminCliBuilder.Create(["sync", "--help"])
            .AddConnector<MockCustomDescriptor>();

        var exitCode = await builder.RunAsync();
        exitCode.Should().Be(0);

        var output = sw.ToString();
        output.Should().Contain("--dry-run");
        output.Should().Contain("--mock-url");
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
}
```

- [ ] **Step 6: Executar testes de Admin para verificar aprovação**

Execute: `dotnet test tests/HcmIdentityProvisioning.Admin.Tests`
Esperado: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/HcmIdentityProvisioning.Admin tests/HcmIdentityProvisioning.Admin.Tests HcmIdentityProvisioning.sln
git commit -m "feat(admin): implement HcmIdentityProvisioning.Admin SDK and CLI builder"
```

---

### Task 3: Refatoração do `HcmIdentityProvisioning.Cli` para Consumir `HcmIdentityProvisioning.Admin` (Dogfooding)

**Files:**
- Modify: `src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj`
- Modify: `src/HcmIdentityProvisioning.Cli/Program.cs`
- Modify: `tests/HcmIdentityProvisioning.Cli.Tests/`

**Interfaces:**
- Produces: Executável `HcmIdentityProvisioning.Cli` limpo consumindo `HcmIdentityProvisioning.Admin`.
- Consumes: `HcmAdminCliBuilder`, `SyntheticHcmConnectorCliDescriptor`, `GenericRestHcmConnectorCliDescriptor`.

- [ ] **Step 1: Atualizar `HcmIdentityProvisioning.Cli.csproj`**

Substituir o conteúdo de `src/HcmIdentityProvisioning.Cli/HcmIdentityProvisioning.Cli.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\HcmIdentityProvisioning.Admin\HcmIdentityProvisioning.Admin.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\fixtures\synthetic-employees.json">
      <Link>fixtures\synthetic-employees.json</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Refatorar `HcmIdentityProvisioning.Cli/Program.cs`**

Substituir `src/HcmIdentityProvisioning.Cli/Program.cs`:
```csharp
using HcmIdentityProvisioning.Admin;
using HcmIdentityProvisioning.Admin.Descriptors;

var app = HcmAdminCliBuilder.Create(args)
    .AddConnector<SyntheticHcmConnectorCliDescriptor>()
    .AddConnector<GenericRestHcmConnectorCliDescriptor>();

return await app.RunAsync();
```

- [ ] **Step 3: Ajustar `HcmIdentityProvisioning.Cli.Tests`**

Atualizar referências e `using` em `tests/HcmIdentityProvisioning.Cli.Tests` para importar `HcmIdentityProvisioning.Admin.Commands` e `HcmIdentityProvisioning.Admin.Utils`.
Adicionar referência de projeto a `HcmIdentityProvisioning.Admin` se necessário em `HcmIdentityProvisioning.Cli.Tests.csproj`.

- [ ] **Step 4: Executar testes de CLI para verificar aprovação**

Execute: `dotnet test tests/HcmIdentityProvisioning.Cli.Tests`
Esperado: PASS (todos os 22 testes passando).

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Cli tests/HcmIdentityProvisioning.Cli.Tests
git commit -m "refactor(cli): consume HcmIdentityProvisioning.Admin in reference CLI application"
```

---

### Task 4: Atualização de `HcmIdentityProvisioning.Functions` com o Fluent Core Builder

**Files:**
- Modify: `src/HcmIdentityProvisioning.Functions/Program.cs`
- Test: `tests/HcmIdentityProvisioning.Functions.Tests/HostCompositionTests.cs`

**Interfaces:**
- Produces: Azure Functions Host composition utilizando `services.AddHcmProvisioning(...)`.

- [ ] **Step 1: Refatorar `src/HcmIdentityProvisioning.Functions/Program.cs`**

Em `src/HcmIdentityProvisioning.Functions/Program.cs`, utilizar o novo método fluente `services.AddHcmProvisioning`:
```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        var settings = new SyncSettings
        {
            TenantDomain = config["SyncSettings:TenantDomain"] ?? "company.onmicrosoft.com",
            ManagedGroupPrefix = config["SyncSettings:ManagedGroupPrefix"] ?? "grp-iam-",
            BatchPageSize = int.TryParse(config["SyncSettings:BatchPageSize"], out var batchSize) ? batchSize : 50,
            MaxDisablementPercentage = double.TryParse(config["SyncSettings:MaxDisablementPercentage"], out var maxPct) ? maxPct : 0.20,
            MaxDisablementCount = int.TryParse(config["SyncSettings:MaxDisablementCount"], out var maxCount) ? maxCount : 10,
            HaltAllOperationsOnTrip = bool.TryParse(config["SyncSettings:HaltAllOperationsOnTrip"], out var halt) && halt
        };

        var rulesPath = config["RULES_FILE_PATH"] ?? "Rules/rules.json";

        services.AddHcmProvisioning(settings, rulesPath)
            .AddEntraIdStore(opts =>
            {
                opts.TenantDomain = settings.TenantDomain;
                opts.ManagedGroupPrefix = settings.ManagedGroupPrefix;
            })
            .AddGenericRestConnector(opts =>
            {
                opts.BaseUrl = config["HCM_REST_BASE_URL"] ?? "https://api.hcm.example.com";
                opts.AuthToken = config["HCM_REST_AUTH_TOKEN"] ?? string.Empty;
            })
            .AddGraphEmailCredentialDelivery(opts =>
            {
                opts.SenderEmail = config["CREDENTIAL_SENDER_EMAIL"] ?? "no-reply@company.onmicrosoft.com";
            });

        services.AddSingleton<GraphServiceClient>(_ => new GraphServiceClient(new DefaultAzureCredential()));
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

await host.RunAsync();
```

- [ ] **Step 2: Executar testes de Functions para verificar aprovação**

Execute: `dotnet test tests/HcmIdentityProvisioning.Functions.Tests`
Esperado: PASS (todos os 5 testes passando).

- [ ] **Step 3: Commit**

```bash
git add src/HcmIdentityProvisioning.Functions/Program.cs
git commit -m "refactor(functions): compose DI using fluent AddHcmProvisioning builder"
```

---

### Task 5: Configuração dos 2 Pacotes Oficiais NuGet, Verificação de Empacotamento e Roadmap

**Files:**
- Modify: `src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj` (`IsPackable: false`)
- Modify: `src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj` (`IsPackable: false`)
- Modify: `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj` (`PackageId: HcmIdentityProvisioning`, `IsPackable: true`)
- Modify: `src/HcmIdentityProvisioning.Admin/HcmIdentityProvisioning.Admin.csproj` (`PackageId: HcmIdentityProvisioning.Admin`, `IsPackable: true`)
- Modify: `docs/superpowers/execution-roadmap.md`

**Interfaces:**
- Produces: Exatamente 2 pacotes `.nupkg` na versão `1.1.0`: `HcmIdentityProvisioning.1.1.0.nupkg` e `HcmIdentityProvisioning.Admin.1.1.0.nupkg`.

- [ ] **Step 1: Ajustar `.csproj` de Domain, Application e Infrastructure**

Em `src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj`:
```xml
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
```

Em `src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj`:
```xml
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
```

Em `src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj`:
```xml
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackageId>HcmIdentityProvisioning</PackageId>
    <Version>1.1.0</Version>
    <Authors>Hcm Identity Provisioning Team</Authors>
    <Description>Enterprise Microsoft Entra ID SCIM/HCM Provisioning & Reconciliation Engine</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryType>git</RepositoryType>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
```

- [ ] **Step 2: Executar `dotnet pack -c Release` e verificar artefatos gerados**

Execute: `dotnet pack -c Release`
Esperado: Exatamente 2 pacotes gerados em `bin/Release/`:
- `HcmIdentityProvisioning.1.1.0.nupkg`
- `HcmIdentityProvisioning.Admin.1.1.0.nupkg`
Nenhum pacote isolado de `Domain` ou `Application` emitido.

- [ ] **Step 3: Executar a suíte de testes completa da solução**

Execute: `dotnet test`
Esperado: 100% de aprovação em todos os projetos de teste da solução (`Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`, `Admin.Tests`, `Cli.Tests`, `Functions.Tests`).

- [ ] **Step 4: Atualizar `docs/superpowers/execution-roadmap.md`**

Registrar no roadmap a transição para a arquitetura de 2 pacotes oficiais de alto nível (`HcmIdentityProvisioning` e `HcmIdentityProvisioning.Admin`).

- [ ] **Step 5: Commit**

```bash
git add src/HcmIdentityProvisioning.Domain/HcmIdentityProvisioning.Domain.csproj src/HcmIdentityProvisioning.Application/HcmIdentityProvisioning.Application.csproj src/HcmIdentityProvisioning.Infrastructure/HcmIdentityProvisioning.Infrastructure.csproj docs/superpowers/execution-roadmap.md
git commit -m "chore(nuget): finalize 2-package architecture for HcmIdentityProvisioning and HcmIdentityProvisioning.Admin"
```
