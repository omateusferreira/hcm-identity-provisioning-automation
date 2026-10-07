using FluentAssertions;
using HcmIdentityProvisioning.Cli.Utils;
using Xunit;

namespace HcmIdentityProvisioning.Cli.Tests;

public class ConsolePrompterTests
{
    [Fact]
    public void ReadMaskedPassword_WhenInputRedirected_ReturnsLineFromStandardInput()
    {
        var prompter = new ConsolePrompter();
        prompter.IsInputRedirected.Should().BeTrue();

        var originalIn = Console.In;
        try
        {
            using var stringReader = new StringReader("MySecretPassword123\n");
            Console.SetIn(stringReader);

            var result = prompter.ReadMaskedPassword("Enter password: ");

            result.Should().Be("MySecretPassword123");
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }
}
