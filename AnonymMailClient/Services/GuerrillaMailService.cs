using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnonymMailClient.Models;
using AnonymMailClient.Tor;

namespace AnonymMailClient.Services;

/// <summary>
/// Service for interacting with Guerrilla Mail (https://api.guerrillamail.com/ajax.php) over Tor SOCKS5.
/// Fully stateless zero-session architecture: No sid_token is ever persisted to disk or stored across requests.
/// Every operation uses a fresh HttpClient connected via an isolated, unique Tor circuit with ephemeral session negotiation.
/// </summary>
public static class GuerrillaMailService
{
    const bool IsLogEnabled = true;
    const string EndpointUrl = "https://api.guerrillamail.com/ajax.php";

    public static readonly string[] AvailableDomains =
    [
        "sharklasers.com",
        "guerrillamail.com",
        "grr.la",
        "guerrillamail.net",
        "guerrillamail.biz",
        "spam4.me",
        "pokemail.net"
    ];

    public static async Task<MailAccount> CreateRandomAccountAsync(string? domain = null, CancellationToken cancellationToken = default)
    {
        domain = string.IsNullOrWhiteSpace(domain) ? AvailableDomains[0] : domain.Trim().ToLowerInvariant();

        Log($"GuerrillaMail: Creating random account on domain '{domain}'...", isRun: IsLogEnabled);
        using var client = TorService.CreateIsolatedHttpClient();

        string url = $"{EndpointUrl}?f=get_email_address&lang=en";
        string json = await client.GetStringAsync(url, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string addr = root.TryGetProperty("email_addr", out var a) ? a.GetString() ?? string.Empty : string.Empty;

        int atIndex = addr.IndexOf('@');
        string emailUser = atIndex > 0 ? addr[..atIndex] : ("anon" + Random.Shared.Next(10000, 99999));

        var account = new MailAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = "guerrilla",
            Username = emailUser,
            Domain = domain,
            EmailAddress = $"{emailUser}@{domain}",
            CreatedAtUtc = DateTime.UtcNow
        };

        Log($"GuerrillaMail: Created random account '{account.EmailAddress}'.", isRun: IsLogEnabled);
        return account;
    }

    public static async Task<MailAccount> CreateCustomAccountAsync(string username, string? domain = null, CancellationToken cancellationToken = default)
    {
        domain = string.IsNullOrWhiteSpace(domain) ? AvailableDomains[0] : domain.Trim().ToLowerInvariant();
        string emailUser = SanitizeUsername(username);

        Log($"GuerrillaMail: Claiming custom account '{emailUser}@{domain}'...", isRun: IsLogEnabled);
        using var client = TorService.CreateIsolatedHttpClient();

        await ClaimUsernameEphemeralAsync(client, emailUser, cancellationToken);

        var account = new MailAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = "guerrilla",
            Username = emailUser,
            Domain = domain,
            EmailAddress = $"{emailUser}@{domain}",
            CreatedAtUtc = DateTime.UtcNow
        };

        Log($"GuerrillaMail: Claimed custom account '{account.EmailAddress}'.", isRun: IsLogEnabled);
        return account;
    }

    public static async Task<IReadOnlyList<MailMessageSummary>> CheckMessagesAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log($"GuerrillaMail: Checking messages for '{account.EmailAddress}' over fresh Tor circuit...", isRun: IsLogEnabled);

        using var client = TorService.CreateIsolatedHttpClient();
        string ephemeralSid = await ClaimUsernameEphemeralAsync(client, account.Username, cancellationToken);

        string url = $"{EndpointUrl}?f=get_email_list&offset=0&sid_token={Uri.EscapeDataString(ephemeralSid)}";
        string json = await client.GetStringAsync(url, cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var items = new List<MailMessageSummary>();
        if (root.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in listElement.EnumerateArray())
            {
                string mailId = ReadStringOrNumber(entry, "mail_id");
                if (string.IsNullOrWhiteSpace(mailId)) continue;

                string rawFrom = ReadString(entry, "mail_from");
                string subject = WebUtility.HtmlDecode(ReadString(entry, "mail_subject"));
                string excerpt = WebUtility.HtmlDecode(ReadString(entry, "mail_excerpt"));
                string readStr = ReadStringOrNumber(entry, "mail_read");
                string attStr = ReadStringOrNumber(entry, "att");
                long timestamp = ReadLong(entry, "mail_timestamp");

                DateTime dateUtc = timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime
                    : DateTime.UtcNow;

                items.Add(new MailMessageSummary
                {
                    Id = mailId,
                    AccountId = account.Id,
                    From = rawFrom,
                    To = account.EmailAddress,
                    Subject = string.IsNullOrWhiteSpace(subject) ? "(No Subject)" : subject,
                    Excerpt = excerpt,
                    DateUtc = dateUtc,
                    IsRead = readStr == "1",
                    HasAttachments = attStr != "0" && !string.IsNullOrEmpty(attStr)
                });
            }
        }

        account.LastCheckedUtc = DateTime.UtcNow;
        account.UnreadCount = items.Count(m => !m.IsRead);

        Log($"GuerrillaMail: Checked '{account.EmailAddress}', found {items.Count} message(s) in {sw.ElapsedMilliseconds}ms (circuit discarded).", isRun: IsLogEnabled);
        return items;
    }

    public static async Task<MailMessageSummary> FetchMessageDetailAsync(MailAccount account, string messageId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log($"GuerrillaMail: Fetching message detail for id='{messageId}' on '{account.EmailAddress}'...", isRun: IsLogEnabled);

        using var client = TorService.CreateIsolatedHttpClient();
        string ephemeralSid = await ClaimUsernameEphemeralAsync(client, account.Username, cancellationToken);

        string url = $"{EndpointUrl}?f=fetch_email&email_id={Uri.EscapeDataString(messageId)}&sid_token={Uri.EscapeDataString(ephemeralSid)}";
        string json = await client.GetStringAsync(url, cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var mail = doc.RootElement;

        string rawFrom = ReadString(mail, "mail_from");
        string subject = WebUtility.HtmlDecode(ReadString(mail, "mail_subject"));
        string body = ReadString(mail, "mail_body");
        string excerpt = WebUtility.HtmlDecode(ReadString(mail, "mail_excerpt"));
        long timestamp = ReadLong(mail, "mail_timestamp");

        var summary = new MailMessageSummary
        {
            Id = messageId,
            AccountId = account.Id,
            From = rawFrom,
            To = account.EmailAddress,
            Subject = string.IsNullOrWhiteSpace(subject) ? "(No Subject)" : subject,
            Excerpt = excerpt,
            BodyHtml = body,
            BodyText = StripHtmlToPlainText(string.IsNullOrWhiteSpace(body) ? excerpt : body),
            DateUtc = timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime : DateTime.UtcNow,
            IsRead = true
        };

        Log($"GuerrillaMail: Fetched message '{messageId}' in {sw.ElapsedMilliseconds}ms (ephemeral session discarded).", isRun: IsLogEnabled);
        return summary;
    }

    public static async Task DeleteMessageAsync(MailAccount account, string messageId, CancellationToken cancellationToken = default)
    {
        using var client = TorService.CreateIsolatedHttpClient();
        string ephemeralSid = await ClaimUsernameEphemeralAsync(client, account.Username, cancellationToken);

        var form = new List<KeyValuePair<string, string>>
        {
            new("f", "del_email"),
            new("sid_token", ephemeralSid),
            new("email_ids[]", messageId)
        };

        using var content = new FormUrlEncodedContent(form);
        using var resp = await client.PostAsync(EndpointUrl, content, cancellationToken);
        resp.EnsureSuccessStatusCode();
        Log($"GuerrillaMail: Deleted message '{messageId}' on '{account.EmailAddress}'.", isRun: IsLogEnabled);
    }

    static async Task<string> ClaimUsernameEphemeralAsync(HttpClient client, string emailUser, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("f", "set_email_user"),
            new("email_user", emailUser),
            new("lang", "en")
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(EndpointUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("sid_token", out var sidProp) && sidProp.GetString() is { Length: > 0 } sid)
            return sid;

        throw new InvalidOperationException($"Guerrilla Mail set_email_user did not return a sid_token for '{emailUser}'.");
    }

    static string SanitizeUsername(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return "anon" + Random.Shared.Next(10000, 99999);
        int at = user.IndexOf('@');
        string local = at >= 0 ? user[..at] : user;
        string cleaned = Regex.Replace(local.Trim().ToLowerInvariant(), @"[^a-z0-9._-]", "");
        return string.IsNullOrWhiteSpace(cleaned) ? "anon" + Random.Shared.Next(10000, 99999) : cleaned;
    }

    static string ReadString(JsonElement element, string prop) =>
        element.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;

    static string ReadStringOrNumber(JsonElement element, string prop)
    {
        if (!element.TryGetProperty(prop, out var p)) return string.Empty;
        if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty;
        if (p.ValueKind == JsonValueKind.Number) return p.GetInt64().ToString();
        return string.Empty;
    }

    static long ReadLong(JsonElement element, string prop)
    {
        if (!element.TryGetProperty(prop, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long v)) return v;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out long s)) return s;
        return 0;
    }

    static string StripHtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        string text = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", Environment.NewLine, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p>", Environment.NewLine + Environment.NewLine, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t]+", " ").Trim();
    }
}
