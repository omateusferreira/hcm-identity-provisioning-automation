using HcmIdentityProvisioning.Domain.Entities;
using HcmIdentityProvisioning.Domain.Ports;
using HcmIdentityProvisioning.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace HcmIdentityProvisioning.Infrastructure.Notifications;

public sealed class GraphEmailCredentialDeliveryService : ICredentialDeliveryService
{
    private readonly GraphServiceClient _graphClient;
    private readonly GraphEmailDeliveryOptions _options;
    private readonly ILogger<GraphEmailCredentialDeliveryService> _logger;

    public GraphEmailCredentialDeliveryService(
        GraphServiceClient graphClient,
        IOptions<GraphEmailDeliveryOptions> options,
        ILogger<GraphEmailCredentialDeliveryService> logger)
    {
        _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DeliverInitialCredentialsAsync(
        Employee employee,
        UserPrincipalName upn,
        string temporaryPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.SenderEmail))
        {
            throw new InvalidOperationException("SenderEmail must be configured in GraphEmailDeliveryOptions.");
        }

        if (!TryGetDeliveryEmail(employee, out var recipientEmail))
        {
            _logger.LogWarning("CREDENTIAL_DELIVERY_SKIPPED: Employee {EmployeeId} has no out-of-band email configured", employee.Id);
            return;
        }

        try
        {
            var encodedName = System.Net.WebUtility.HtmlEncode(employee.FullName);
            var encodedUpn = System.Net.WebUtility.HtmlEncode(upn.Value);
            var encodedPassword = System.Net.WebUtility.HtmlEncode(temporaryPassword);

            var requestBody = new SendMailPostRequestBody
            {
                Message = new Message
                {
                    Subject = _options.Subject,
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient
                        {
                            EmailAddress = new Microsoft.Graph.Models.EmailAddress
                            {
                                Address = recipientEmail
                            }
                        }
                    },
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = $@"
                            <p>Olá <strong>{encodedName}</strong>,</p>
                            <p>Sua conta corporativa foi provisionada no Microsoft Entra ID:</p>
                            <ul>
                                <li><strong>Usuário (UPN):</strong> {encodedUpn}</li>
                                <li><strong>Senha Temporária:</strong> {encodedPassword}</li>
                            </ul>
                            <p>No primeiro login, você deverá obrigatoriamente redefinir esta senha.</p>
                            <p>Acesse o portal: <a href=""https://myapplications.microsoft.com"">https://myapplications.microsoft.com</a></p>"
                    }
                },
                SaveToSentItems = _options.SaveToSentItems
            };

            await _graphClient.Users[_options.SenderEmail]
                .SendMail
                .PostAsync(requestBody, cancellationToken: ct);

            _logger.LogInformation(
                "CREDENTIAL_DELIVERY_SUCCESS: Dispatched initial credentials for employee {EmployeeId} ({Upn}) to {RecipientEmail}",
                employee.Id, upn, recipientEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "CREDENTIAL_DELIVERY_FAILED: Error delivering credentials for employee {EmployeeId} to {RecipientEmail}",
                employee.Id, recipientEmail);
        }
    }

    private static bool TryGetDeliveryEmail(Employee employee, out string email)
    {
        if (employee.ExtendedAttributes != null)
        {
            foreach (var kvp in employee.ExtendedAttributes)
            {
                if (string.Equals(kvp.Key, "email", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    email = kvp.Value.Trim();
                    return true;
                }
            }
        }

        email = string.Empty;
        return false;
    }
}
