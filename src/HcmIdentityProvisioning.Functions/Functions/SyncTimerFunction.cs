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
