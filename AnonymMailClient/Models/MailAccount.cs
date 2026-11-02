namespace AnonymMailClient.Models;

/// <summary>
/// Represents a provisioned Guerrilla Mail account managed by this client.
/// </summary>
public sealed class MailAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EmailAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = "sharklasers.com";
    public string SidToken { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedUtc { get; set; }
    public int UnreadCount { get; set; }

    public override string ToString() => EmailAddress;
}
