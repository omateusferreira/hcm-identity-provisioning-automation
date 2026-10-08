using FluentAssertions;
using HcmIdentityProvisioning.Admin.Utils;
using Xunit;

namespace HcmIdentityProvisioning.Admin.Tests.Utils;

public class PathResolverTests
{
    [Fact]
    public void ResolveRulesPath_WithNonExistentPath_ThrowsFileNotFoundException()
    {
        var action = () => PathResolver.ResolveRulesPath("non-existent-rules-file-xyz.json");
        action.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ResolveFixturesPath_WithNonExistentPath_ThrowsFileNotFoundException()
    {
        var action = () => PathResolver.ResolveFixturesPath("non-existent-fixtures-file-xyz.json");
        action.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ResolveRulesPath_WithValidExistingPath_ReturnsFullPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var resolved = PathResolver.ResolveRulesPath(tempFile);
            resolved.Should().Be(Path.GetFullPath(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveFixturesPath_WithValidExistingPath_ReturnsFullPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var resolved = PathResolver.ResolveFixturesPath(tempFile);
            resolved.Should().Be(Path.GetFullPath(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
