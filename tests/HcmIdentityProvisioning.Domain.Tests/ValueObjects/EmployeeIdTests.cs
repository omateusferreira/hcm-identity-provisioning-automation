using FluentAssertions;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.ValueObjects;

public class EmployeeIdTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidValue_ShouldFail(string? raw)
    {
        var result = EmployeeId.Create(raw);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_WithValidValue_ShouldSucceed()
    {
        var result = EmployeeId.Create("EMP-00123");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("EMP-00123");
        result.Value.ToString().Should().Be("EMP-00123");
    }

    [Fact]
    public void Create_WithWhitespacePadding_ShouldTrim()
    {
        var result = EmployeeId.Create("  EMP-00123  ");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("EMP-00123");
    }

    [Fact]
    public void Equals_WithSameValue_ShouldBeEqual()
    {
        var id1 = EmployeeId.Create("EMP-00123").Value;
        var id2 = EmployeeId.Create("EMP-00123").Value;
        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
    }
}
