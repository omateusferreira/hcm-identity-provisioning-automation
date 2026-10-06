using FluentAssertions;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.ValueObjects;

public class UserPrincipalNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-upn")]
    [InlineData("@company.com")]
    [InlineData("user@")]
    [InlineData("user@company")]
    [InlineData("user@@company.com")]
    public void Create_WithInvalidFormat_ShouldFail(string? raw)
    {
        var result = UserPrincipalName.Create(raw);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_WithValidUpn_ShouldNormalizeToLowercase()
    {
        var result = UserPrincipalName.Create("John.Doe@Company.COM");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("john.doe@company.com");
        result.Value.Username.Should().Be("john.doe");
        result.Value.Domain.Should().Be("company.com");
        result.Value.ToString().Should().Be("john.doe@company.com");
    }

    [Fact]
    public void Create_WithWhitespacePadding_ShouldTrimAndNormalize()
    {
        var result = UserPrincipalName.Create("  Jane.Smith@Example.COM  ");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("jane.smith@example.com");
        result.Value.Username.Should().Be("jane.smith");
        result.Value.Domain.Should().Be("example.com");
    }

    [Fact]
    public void Equals_WithSameValueDifferentCasing_ShouldBeEqual()
    {
        var upn1 = UserPrincipalName.Create("User@Corp.com").Value;
        var upn2 = UserPrincipalName.Create("USER@CORP.COM").Value;
        upn1.Should().Be(upn2);
        (upn1 == upn2).Should().BeTrue();
    }
}
