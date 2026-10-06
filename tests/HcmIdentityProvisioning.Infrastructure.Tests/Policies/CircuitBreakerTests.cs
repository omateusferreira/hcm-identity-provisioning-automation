using FluentAssertions;
using HcmIdentityProvisioning.Domain.Actions;
using HcmIdentityProvisioning.Infrastructure.Policies;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Policies;

public class CircuitBreakerTests
{
    [Fact]
    public void ShouldTrip_WhenDisablePercentageExceeded_ReturnsTrue()
    {
        var breaker = new DisablementCircuitBreaker(maxDisablePercentage: 10.0, maxDisableCount: 25);
        var actions = new DeltaAction[]
        {
            new DisableAccountAction(Guid.NewGuid()),
            new DisableAccountAction(Guid.NewGuid())
        };

        var tripped = breaker.ShouldTrip(totalBatchSize: 10, proposedActions: actions, out var reason);

        tripped.Should().BeTrue();
        reason.Should().Contain("Rate 20.00% exceeds threshold 10.00%");
    }

    [Fact]
    public void ShouldTrip_WhenWithinSafeThreshold_ReturnsFalse()
    {
        var breaker = new DisablementCircuitBreaker(maxDisablePercentage: 10.0, maxDisableCount: 25);
        var actions = new DeltaAction[]
        {
            new DisableAccountAction(Guid.NewGuid())
        };

        var tripped = breaker.ShouldTrip(totalBatchSize: 50, proposedActions: actions, out var reason);

        tripped.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void ShouldTrip_WhenDisableCountExceedsAbsoluteLimit_ReturnsTrue()
    {
        var breaker = new DisablementCircuitBreaker(maxDisablePercentage: 10.0, maxDisableCount: 25);
        var actions = Enumerable.Range(0, 26)
            .Select(_ => new DisableAccountAction(Guid.NewGuid()))
            .ToArray();

        var tripped = breaker.ShouldTrip(totalBatchSize: 1000, proposedActions: actions, out var reason);

        tripped.Should().BeTrue();
        reason.Should().Contain("Disablement Count 26 exceeds absolute limit 25");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ShouldTrip_WhenBatchSizeZeroOrNegative_ReturnsFalse(int batchSize)
    {
        var breaker = new DisablementCircuitBreaker();
        var actions = new DeltaAction[]
        {
            new DisableAccountAction(Guid.NewGuid())
        };

        var tripped = breaker.ShouldTrip(totalBatchSize: batchSize, proposedActions: actions, out var reason);

        tripped.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void ShouldTrip_WhenNoDisableActions_ReturnsFalse()
    {
        var breaker = new DisablementCircuitBreaker();
        var actions = new DeltaAction[]
        {
            new EnableAccountAction(Guid.NewGuid())
        };

        var tripped = breaker.ShouldTrip(totalBatchSize: 10, proposedActions: actions, out var reason);

        tripped.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_DefaultValues_Are10PercentAnd25Count()
    {
        var breaker = new DisablementCircuitBreaker();

        breaker.MaxDisablePercentage.Should().Be(10.0);
        breaker.MaxDisableCount.Should().Be(25);
    }
}
