using FluentAssertions;
using HcmIdentityProvisioning.Application.Options;
using HcmIdentityProvisioning.Application.Services;
using HcmIdentityProvisioning.Application.UseCases;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Domain.Common;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Policies;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Functions.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HcmIdentityProvisioning.Functions.Tests.Functions;

public class SyncTimerFunctionTests
{
    private readonly IHcmConnector _connector = Substitute.For<IHcmConnector>();
    private readonly IIdentityStore _identityStore = Substitute.For<IIdentityStore>();
    private readonly IRulesEngine _rulesEngine = Substitute.For<IRulesEngine>();
    private readonly ISecurePasswordGenerator _passwordGenerator = Substitute.For<ISecurePasswordGenerator>();
    private readonly ICircuitBreaker _circuitBreaker = Substitute.For<ICircuitBreaker>();
    private readonly ICredentialDeliveryService _credentials = Substitute.For<ICredentialDeliveryService>();
    private readonly ILogger<SyncTimerFunction> _logger = Substitute.For<ILogger<SyncTimerFunction>>();
    private readonly FunctionContext _context = Substitute.For<FunctionContext>();

    private ReconcileBatchUseCase CreateUseCase()
    {
        var reconciler = new IdentityReconciliationService(_rulesEngine, _passwordGenerator);
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
            .Returns(new PagedResult<Employee>(new List<Employee>(), 1, 50, 0, false));

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
        var employee = new Employee(
            EmployeeId.Create("EMP1").Value,
            "Jane Doe",
            EmployeeStatus.Inactive,
            "HR",
            "Specialist",
            new Dictionary<string, string>());

        _connector.GetEmployeesPageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Employee>(new List<Employee> { employee }, 1, 50, 1, false));

        var user = new EntraUser(
            Guid.NewGuid(),
            employee.Id,
            UserPrincipalName.Create("jane.doe@company.onmicrosoft.com").Value,
            "Jane Doe",
            true,
            new HashSet<Guid>());

        _identityStore.GetUsersByEmployeeIdsAsync(Arg.Any<IEnumerable<EmployeeId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<EmployeeId, EntraUser> { [employee.Id] = user });

        _circuitBreaker.ShouldTrip(Arg.Any<int>(), Arg.Any<IReadOnlyList<DeltaAction>>(), out Arg.Any<string>())
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
            .ThrowsAsync(new HttpRequestException("Network failure"));

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
