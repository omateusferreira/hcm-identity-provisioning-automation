using FluentAssertions;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Xunit;

namespace HcmIdentityProvisioning.Domain.Tests.ValueObjects;

public class EmailAddressTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plainaddress")]
    [InlineData("@missingusername.com")]
    [InlineData("missingdomain@")]
    [InlineData("missingdot@domain")]
    public void Create_WithInvalidValue_ShouldFail(string? raw)
    {
        var result = EmailAddress.Create(raw);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_WithValidEmail_ShouldNormalizeToLowercase()
    {
        var result = EmailAddress.Create("Jane.Doe@Company.COM");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("jane.doe@company.com");
        result.Value.ToString().Should().Be("jane.doe@company.com");
    }

    [Fact]
    public void Create_WithWhitespacePadding_ShouldTrimAndNormalize()
    {
        var result = EmailAddress.Create("  jane.doe@company.com  ");
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("jane.doe@company.com");
    }

    [Fact]
    public void Equals_WithSameValueDifferentCasing_ShouldBeEqual()
    {
        var email1 = EmailAddress.Create("Jane@Corp.com").Value;
        var email2 = EmailAddress.Create("JANE@CORP.COM").Value;
        email1.Should().Be(email2);
        (email1 == email2).Should().BeTrue();
    }
}
