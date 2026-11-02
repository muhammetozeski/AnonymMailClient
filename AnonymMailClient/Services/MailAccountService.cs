using AnonymMailClient.Models;

namespace AnonymMailClient.Services;

/// <summary>
/// Dispatches mail checking, message retrieval, and deletion to the appropriate provider (Guerrilla Mail or Mail.tm).
/// </summary>
public static class MailAccountService
{
    public static async Task<IReadOnlyList<MailMessageSummary>> CheckMessagesAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (string.Equals(account.Provider, "mailtm", StringComparison.OrdinalIgnoreCase))
            return await MailTmService.CheckMessagesAsync(account, cancellationToken);

        return await GuerrillaMailService.CheckMessagesAsync(account, cancellationToken);
    }

    public static async Task<MailMessageSummary> FetchMessageDetailAsync(MailAccount account, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(account.Provider, "mailtm", StringComparison.OrdinalIgnoreCase))
            return await MailTmService.FetchMessageDetailAsync(account, messageId, cancellationToken);

        return await GuerrillaMailService.FetchMessageDetailAsync(account, messageId, cancellationToken);
    }

    public static async Task DeleteMessageAsync(MailAccount account, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(account.Provider, "mailtm", StringComparison.OrdinalIgnoreCase))
            await MailTmService.DeleteMessageAsync(account, messageId, cancellationToken);
        else
            await GuerrillaMailService.DeleteMessageAsync(account, messageId, cancellationToken);
    }
}
