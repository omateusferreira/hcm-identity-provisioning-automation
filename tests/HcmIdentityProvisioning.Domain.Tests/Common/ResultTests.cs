using FluentAssertions;
using HcmIdentityProvisioning.Domain.Common;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Success_ShouldHoldValue_AndHaveIsSuccessTrue()
    {
        var result = Result<int, string>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_ShouldHoldError_AndHaveIsSuccessFalse()
    {
        var result = Result<int, string>.Failure("Operation failed");

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().Be(0);
        result.Error.Should().Be("Operation failed");
    }
}
