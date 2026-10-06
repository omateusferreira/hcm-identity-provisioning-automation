using FluentAssertions;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Security;

public class UpnSanitizerTests
{
    [Theory]
    [InlineData("José d'Ávila", "jose.davila")]
    [InlineData("Carlos Eduardo dos Santos", "carlos.santos")]
    [InlineData("Maria João-Gonçalves", "maria.joao-goncalves")]
    [InlineData("Ana  Paula   Menezes", "ana.menezes")]
    public void Sanitize_ShouldStripAccentsAndFormSlug(string fullName, string expectedSlug)
    {
        var slug = UpnSanitizer.SanitizeNameToSlug(fullName);
        slug.Should().Be(expectedSlug);
    }

    [Fact]
    public async Task ResolveAvailableUpnAsync_WhenCollisionExists_AppendsIncrement()
    {
        var store = Substitute.For<IIdentityStore>();
        store.IsUserPrincipalNameAvailableAsync(Arg.Is<UserPrincipalName>(u => u.Value == "john.doe@corp.com"))
            .Returns(false);
        store.IsUserPrincipalNameAvailableAsync(Arg.Is<UserPrincipalName>(u => u.Value == "john.doe2@corp.com"))
            .Returns(true);

        var upn = await UpnSanitizer.ResolveAvailableUpnAsync("john.doe", "corp.com", store);

        upn.Value.Should().Be("john.doe2@corp.com");
    }

    [Fact]
    public async Task ResolveAvailableUpnAsync_WhenNoCollision_ReturnsBaseUpn()
    {
        var store = Substitute.For<IIdentityStore>();
        store.IsUserPrincipalNameAvailableAsync(Arg.Is<UserPrincipalName>(u => u.Value == "john.doe@corp.com"))
            .Returns(true);

        var upn = await UpnSanitizer.ResolveAvailableUpnAsync("john.doe", "corp.com", store);

        upn.Value.Should().Be("john.doe@corp.com");
    }

    [Theory]
    [InlineData(null, "user")]
    [InlineData("", "user")]
    [InlineData("   ", "user")]
    [InlineData("Madonna", "madonna")]
    public void Sanitize_EdgeCases_ReturnsExpectedSlug(string? fullName, string expectedSlug)
    {
        var slug = UpnSanitizer.SanitizeNameToSlug(fullName!);
        slug.Should().Be(expectedSlug);
    }
}
