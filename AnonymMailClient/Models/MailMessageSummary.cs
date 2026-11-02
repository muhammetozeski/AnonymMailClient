namespace AnonymMailClient.Models;

/// <summary>
/// Cached summary and full content of a received email message.
/// </summary>
public sealed class MailMessageSummary
{
    public string Id { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; } = DateTime.UtcNow;
    public string Excerpt { get; set; } = string.Empty;
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }

    public string DateDisplay => DateUtc.ToLocalTime().ToString("g");
}
