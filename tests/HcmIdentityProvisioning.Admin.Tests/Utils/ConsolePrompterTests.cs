using FluentAssertions;
using HcmIdentityProvisioning.Admin.Utils;
using Xunit;

namespace HcmIdentityProvisioning.Admin.Tests.Utils;

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

    [Fact]
    public async Task ConfirmAsync_WhenInputRedirected_ReturnsDefaultResponse()
    {
        var prompter = new ConsolePrompter();
        var resultFalse = await prompter.ConfirmAsync("Confirm action?", defaultResponse: false);
        resultFalse.Should().BeFalse();

        var resultTrue = await prompter.ConfirmAsync("Confirm action?", defaultResponse: true);
        resultTrue.Should().BeTrue();
    }

    [Fact]
    public async Task PromptAsync_WhenInputRedirected_ReturnsLineOrDefault()
    {
        var prompter = new ConsolePrompter();
        var originalIn = Console.In;
        try
        {
            using var stringReader = new StringReader("CustomValue\n");
            Console.SetIn(stringReader);

            var result = await prompter.PromptAsync("Enter value: ", defaultValue: "DefaultVal");
            result.Should().Be("CustomValue");
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }
}
