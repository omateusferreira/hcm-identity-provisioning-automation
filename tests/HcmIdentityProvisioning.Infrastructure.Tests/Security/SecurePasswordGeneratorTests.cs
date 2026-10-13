using FluentAssertions;
using HcmIdentityProvisioning.Infrastructure.Security;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Security;

public class SecurePasswordGeneratorTests
{
    [Fact]
    public void GeneratePassword_ShouldSatisfyComplexityRules()
    {
        var generator = new SecurePasswordGenerator();
        var password = generator.GeneratePassword(24);

        password.Length.Should().Be(24);
        password.Should().MatchRegex(@"[A-Z]");
        password.Should().MatchRegex(@"[a-z]");
        password.Should().MatchRegex(@"[0-9]");
        password.Should().MatchRegex(@"[!@#$%^&*()_+\-=\[\]{}|;:,.<>?]");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public void GeneratePassword_WithCustomValidLength_ReturnsPasswordOfGivenLength(int length)
    {
        var generator = new SecurePasswordGenerator();
        var password = generator.GeneratePassword(length);

        password.Length.Should().Be(length);
        password.Should().MatchRegex(@"[A-Z]");
        password.Should().MatchRegex(@"[a-z]");
        password.Should().MatchRegex(@"[0-9]");
        password.Should().MatchRegex(@"[!@#$%^&*()_+\-=\[\]{}|;:,.<>?]");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    public void GeneratePassword_WhenLengthLessThan12_EnforcesMinimumLengthOf12(int shortLength)
    {
        var generator = new SecurePasswordGenerator();
        var password = generator.GeneratePassword(shortLength);

        password.Length.Should().Be(12);
        password.Should().MatchRegex(@"[A-Z]");
        password.Should().MatchRegex(@"[a-z]");
        password.Should().MatchRegex(@"[0-9]");
        password.Should().MatchRegex(@"[!@#$%^&*()_+\-=\[\]{}|;:,.<>?]");
    }
}
