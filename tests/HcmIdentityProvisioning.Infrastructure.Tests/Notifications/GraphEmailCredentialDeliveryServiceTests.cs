using System.Net;
using FluentAssertions;
using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Enums;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using HcmIdentityProvisioning.Infrastructure.DependencyInjection;
using HcmIdentityProvisioning.Infrastructure.Notifications;
using HcmIdentityProvisioning.Infrastructure.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Xunit;

namespace HcmIdentityProvisioning.Infrastructure.Tests.Notifications;

public class GraphEmailCredentialDeliveryServiceTests
{
    private static GraphServiceClient CreateMockGraphClient(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        return new GraphServiceClient(httpClient, new AnonymousAuthenticationProvider());
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenSenderEmailEmpty_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler();
        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions { SenderEmail = "" });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP01").Value,
            "John Doe",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string> { ["email"] = "john.doe@external.com" });

        var upn = UserPrincipalName.Create("john.doe@company.onmicrosoft.com").Value;

        var act = async () => await service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SenderEmail*");
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmailAttributeMissing_SkipsWithoutCallingGraph()
    {
        var handler = new MockHttpMessageHandler();
        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions { SenderEmail = "no-reply@company.com" });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP01").Value,
            "John Doe",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string>()); // sem chave "email"

        var upn = UserPrincipalName.Create("john.doe@company.onmicrosoft.com").Value;

        await service.DeliverInitialCredentialsAsync(employee, upn, "TempPass123!");

        handler.SentRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmailPresent_SendsMailViaGraphApi()
    {
        var handler = new MockHttpMessageHandler();
        handler.RegisterResponse("POST", "users/no-reply@company.com/sendMail", HttpStatusCode.Accepted, "{}");

        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions
        {
            SenderEmail = "no-reply@company.com",
            Subject = "Bem-vindo",
            SaveToSentItems = false
        });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP01").Value,
            "John Doe",
            EmployeeStatus.Active,
            "Engineering",
            "Software Engineer",
            new Dictionary<string, string> { ["email"] = "john.recipient@external.com" });

        var upn = UserPrincipalName.Create("john.doe@company.onmicrosoft.com").Value;

        await service.DeliverInitialCredentialsAsync(employee, upn, "SecretTempPass123!");

        handler.SentRequests.Should().HaveCount(1);
        var request = handler.SentRequests[0];
        request.RequestUri!.ToString().Should().Contain("users/no-reply@company.com/sendMail");
        request.Content.Should().NotBeNull();
        var content = await request.Content!.ReadAsStringAsync();
        content.Should().Contain("john.recipient@external.com");
        content.Should().Contain("john.doe@company.onmicrosoft.com");
        content.Should().Contain("SecretTempPass123!");
    }

    [Fact]
    public async Task DeliverInitialCredentialsAsync_WhenEmailKeyHasDifferentCasing_SendsMailSuccessfully()
    {
        var handler = new MockHttpMessageHandler();
        handler.RegisterResponse("POST", "users/no-reply@company.com/sendMail", HttpStatusCode.Accepted, "{}");

        var graphClient = CreateMockGraphClient(handler);
        var options = Options.Create(new GraphEmailDeliveryOptions
        {
            SenderEmail = "no-reply@company.com",
            Subject = "Bem-vindo",
            SaveToSentItems = false
        });
        var service = new GraphEmailCredentialDeliveryService(graphClient, options, NullLogger<GraphEmailCredentialDeliveryService>.Instance);

        var employee = new Employee(
            EmployeeId.Create("EMP02").Value,
            "Jane Smith",
            EmployeeStatus.Active,
            "Marketing",
            "Manager",
            new Dictionary<string, string> { ["EMAIL"] = "jane.smith@external.com" });

        var upn = UserPrincipalName.Create("jane.smith@company.onmicrosoft.com").Value;

        await service.DeliverInitialCredentialsAsync(employee, upn, "TempPass789!");

        handler.SentRequests.Should().HaveCount(1);
        var content = await handler.SentRequests[0].Content!.ReadAsStringAsync();
        content.Should().Contain("jane.smith@external.com");
    }

    [Fact]
    public void AddGraphEmailCredentialDeliveryService_RegistersServiceCorrectly()
    {
        var services = new ServiceCollection();
        var handler = new MockHttpMessageHandler();
        var graphClient = CreateMockGraphClient(handler);
        services.AddSingleton(graphClient);

        services.AddGraphEmailCredentialDeliveryService(options =>
        {
            options.SenderEmail = "admin@company.com";
        });

        using var provider = services.BuildServiceProvider();
        var deliveryService = provider.GetRequiredService<ICredentialDeliveryService>();

        deliveryService.Should().NotBeNull();
        deliveryService.Should().BeOfType<GraphEmailCredentialDeliveryService>();
    }
}
