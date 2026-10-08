using System.CommandLine;
using System.CommandLine.Parsing;
using Azure.Identity;
using HcmIdentityProvisioning.Admin.Commands;
using HcmIdentityProvisioning.Admin.Extensibility;
using HcmIdentityProvisioning.Admin.Utils;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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

    public static HcmAdminCliBuilder Create(string[]? args) => new(args ?? Array.Empty<string>());

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

        // Subcomando sync com factory de DI dinâmica e descritores
        var syncCmd = SyncCommand.Create(
            (opts, parseResult) => BuildServiceProvider(opts, parseResult, isVerbose),
            _prompter,
            _descriptors);
        rootCommand.AddCommand(syncCmd);

        // Subcomando ensure-groups
        var ensureCmd = EnsureGroupsCommand.Create(
            opts => BuildServiceProvider(opts, null, isVerbose),
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

    public IServiceProvider BuildServiceProvider(SyncCliOptions options, ParseResult? parseResult = null, bool isVerbose = false)
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

        // Executar descritores
        if (parseResult != null)
        {
            foreach (var descriptor in _descriptors)
            {
                descriptor.ConfigureServices(services, parseResult, options);
            }
        }

        // Aplicar customizações de serviços registradas pelo usuário
        foreach (var config in _serviceConfigurators)
        {
            config(services);
        }

        // Se nenhum conector tiver sido registrado por descriptor ou serviço, registra SyntheticConnector como default
        if (!services.Any(sd => sd.ServiceType == typeof(IHcmConnector)))
        {
            var fixturesPath = PathResolver.ResolveFixturesPath(options.FixturesPath);
            services.AddSyntheticHcmConnector(fixturesPath);
        }

        return services.BuildServiceProvider();
    }
}
