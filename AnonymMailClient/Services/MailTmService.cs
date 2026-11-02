using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnonymMailClient.Models;
using AnonymMailClient.Tor;
using MimeKit;

namespace AnonymMailClient.Services;

/// <summary>
/// Service for interacting with Mail.tm (https://api.mail.tm) strictly over Tor SOCKS5.
/// Supports account provisioning, domain listing, JWT token management, message polling, and detail retrieval.
/// </summary>
public static class MailTmService
{
    const bool IsLogEnabled = true;
    const string BaseUrl = "https://api.mail.tm";

    sealed record TokenEntry(string Token, string AccountRemoteId, DateTime ObtainedUtc);

    static readonly ConcurrentDictionary<string, TokenEntry> TokensByAccountId = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(45);

    public static async Task<IReadOnlyList<string>> GetDomainsAsync(string isolationKey = "mailtm_discovery", CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log($"MailTm.GetDomainsAsync started; isolationKey='{isolationKey}'", isRun: IsLogEnabled);

        using var client = TorService.CreateIsolatedHttpClient(isolationKey, timeout: TimeSpan.FromSeconds(35));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/domains");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var domains = new List<string>();

        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            bool isActive = !item.TryGetProperty("isActive", out var activeProp) || activeProp.ValueKind != JsonValueKind.False;
            if (!isActive) continue;

            if (item.TryGetProperty("domain", out var domProp) && domProp.GetString() is { Length: > 0 } domain)
                domains.Add(domain.Trim().ToLowerInvariant());
        }

        Log($"MailTm.GetDomainsAsync completed in {sw.ElapsedMilliseconds}ms: [{string.Join(", ", domains)}]", isRun: IsLogEnabled);
        return domains;
    }

    public static async Task<MailAccount> CreateAccountAsync(
        string? desiredUsername,
        string? desiredDomain,
        string? password = null,
        string? isolationKey = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        string key = isolationKey ?? Guid.NewGuid().ToString("N");

        string domain = desiredDomain?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(domain))
        {
            var domains = await GetDomainsAsync(key, cancellationToken);
            domain = domains.FirstOrDefault() ?? throw new InvalidOperationException("Mail.tm returned no active domains.");
        }

        string username = !string.IsNullOrWhiteSpace(desiredUsername)
            ? SanitizeUsername(desiredUsername)
            : GenerateRandomUsername();

        string finalPassword = !string.IsNullOrWhiteSpace(password)
            ? password
            : GenerateSecurePassword();

        string email = $"{username}@{domain}";
        Log($"MailTm.CreateAccountAsync started for '{email}'", isRun: IsLogEnabled);

        using var client = TorService.CreateIsolatedHttpClient(key, timeout: TimeSpan.FromSeconds(40));
        string payload = JsonSerializer.Serialize(new { address = email, password = finalPassword });

        using (var createReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/accounts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        })
        {
            createReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
            using var createResp = await client.SendAsync(createReq, cancellationToken);

            if (!createResp.IsSuccessStatusCode && createResp.StatusCode != HttpStatusCode.UnprocessableEntity)
            {
                string detail = await createResp.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Mail.tm rejected account creation ({(int)createResp.StatusCode}): {ExtractErrorMessage(detail)}");
            }
        }

        var tokenEntry = await ObtainTokenAsync(client, email, finalPassword, cancellationToken);

        var account = new MailAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = "mailtm",
            EmailAddress = email,
            Username = username,
            Domain = domain,
            Password = finalPassword,
            RemoteAccountId = tokenEntry.AccountRemoteId,
            CreatedAtUtc = DateTime.UtcNow
        };

        TokensByAccountId[account.Id] = tokenEntry;
        Log($"MailTm.CreateAccountAsync succeeded for '{email}' in {sw.ElapsedMilliseconds}ms", isRun: IsLogEnabled);
        return account;
    }

    public static async Task<IReadOnlyList<MailMessageSummary>> CheckMessagesAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        using var client = TorService.CreateIsolatedHttpClient(account.Id, timeout: TimeSpan.FromSeconds(35));
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);

        using var response = await SendAuthorizedAsync(client, account, HttpMethod.Get, $"{BaseUrl}/messages?page=1", null, token, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var result = new List<MailMessageSummary>();

        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            string id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            string fromAddress = string.Empty;
            if (item.TryGetProperty("from", out var fromObj) && fromObj.ValueKind == JsonValueKind.Object)
            {
                fromAddress = ReadString(fromObj, "address");
                string name = ReadString(fromObj, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    fromAddress = $"{name} <{fromAddress}>";
            }

            string subject = ReadString(item, "subject");
            string intro = ReadString(item, "intro");
            bool seen = item.TryGetProperty("seen", out var seenProp) && seenProp.ValueKind == JsonValueKind.True;

            DateTime dateUtc = DateTime.UtcNow;
            if (DateTimeOffset.TryParse(ReadString(item, "createdAt"), out var parsedDate))
                dateUtc = parsedDate.UtcDateTime;

            result.Add(new MailMessageSummary
            {
                Id = id,
                AccountId = account.Id,
                From = fromAddress,
                Subject = string.IsNullOrWhiteSpace(subject) ? "(No Subject)" : subject,
                DateUtc = dateUtc,
                IsRead = seen,
                Excerpt = intro
            });
        }

        account.LastCheckedUtc = DateTime.UtcNow;
        account.UnreadCount = result.Count(static m => !m.IsRead);
        Log($"MailTm.CheckMessagesAsync: account='{account.EmailAddress}', found={result.Count} msgs, unread={account.UnreadCount} ({sw.ElapsedMilliseconds}ms)", isRun: IsLogEnabled);
        return result;
    }

    public static async Task<MailMessageSummary> FetchMessageDetailAsync(MailAccount account, string messageId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        using var client = TorService.CreateIsolatedHttpClient(account.Id, timeout: TimeSpan.FromSeconds(40));
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);

        // Try raw MIME source download first
        try
        {
            using var sourceResp = await SendAuthorizedAsync(client, account, HttpMethod.Get, $"{BaseUrl}/sources/{Uri.EscapeDataString(messageId)}", null, token, cancellationToken);
            string sourceJson = await sourceResp.Content.ReadAsStringAsync(cancellationToken);
            using var sourceDoc = JsonDocument.Parse(sourceJson);
            string rawMime = ReadString(sourceDoc.RootElement, "data");

            if (!string.IsNullOrWhiteSpace(rawMime))
            {
                await using var rawStream = new MemoryStream(Encoding.UTF8.GetBytes(rawMime));
                var parsedMime = await MimeMessage.LoadAsync(rawStream, cancellationToken);

                string body = !string.IsNullOrWhiteSpace(parsedMime.TextBody)
                    ? parsedMime.TextBody
                    : (parsedMime.HtmlBody ?? "(Empty message body)");

                return new MailMessageSummary
                {
                    Id = messageId,
                    AccountId = account.Id,
                    From = parsedMime.From.ToString(),
                    Subject = parsedMime.Subject ?? "(No Subject)",
                    DateUtc = parsedMime.Date.UtcDateTime,
                    IsRead = true,
                    BodyText = body
                };
            }
        }
        catch (Exception ex)
        {
            Log($"MailTm /sources/{messageId} fallback to /messages: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
        }

        // Fallback to JSON /messages/{id}
        using var msgResp = await SendAuthorizedAsync(client, account, HttpMethod.Get, $"{BaseUrl}/messages/{Uri.EscapeDataString(messageId)}", null, token, cancellationToken);
        string msgJson = await msgResp.Content.ReadAsStringAsync(cancellationToken);
        using var msgDoc = JsonDocument.Parse(msgJson);
        var root = msgDoc.RootElement;

        string fromAddr = "";
        if (root.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object)
            fromAddr = ReadString(f, "address");

        string subj = ReadString(root, "subject");
        string text = ReadString(root, "text");
        DateTime dt = DateTime.UtcNow;
        if (DateTimeOffset.TryParse(ReadString(root, "createdAt"), out var p))
            dt = p.UtcDateTime;

        Log($"MailTm.FetchMessageDetailAsync completed for {messageId} in {sw.ElapsedMilliseconds}ms", isRun: IsLogEnabled);
        return new MailMessageSummary
        {
            Id = messageId,
            AccountId = account.Id,
            From = fromAddr,
            Subject = string.IsNullOrWhiteSpace(subj) ? "(No Subject)" : subj,
            DateUtc = dt,
            IsRead = true,
            BodyText = string.IsNullOrWhiteSpace(text) ? "(No text content)" : text
        };
    }

    public static async Task DeleteMessageAsync(MailAccount account, string messageId, CancellationToken cancellationToken = default)
    {
        using var client = TorService.CreateIsolatedHttpClient(account.Id, timeout: TimeSpan.FromSeconds(30));
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);
        using var resp = await SendAuthorizedAsync(client, account, HttpMethod.Delete, $"{BaseUrl}/messages/{Uri.EscapeDataString(messageId)}", null, token, cancellationToken);
    }

    static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        MailAccount account,
        HttpMethod method,
        string url,
        HttpContent? content,
        string token,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));

        var resp = await client.SendAsync(req, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            string refreshed = await EnsureBearerTokenAsync(client, account, forceRefresh: true, cancellationToken);
            using var retryReq = new HttpRequestMessage(method, url);
            if (content != null)
            {
                string bytes = await content.ReadAsStringAsync(cancellationToken);
                retryReq.Content = new StringContent(bytes, Encoding.UTF8, content.Headers.ContentType?.MediaType ?? "application/json");
            }
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
            retryReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
            resp = await client.SendAsync(retryReq, cancellationToken);
        }

        resp.EnsureSuccessStatusCode();
        return resp;
    }

    static async Task<string> EnsureBearerTokenAsync(HttpClient client, MailAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh &&
            TokensByAccountId.TryGetValue(account.Id, out var cached) &&
            DateTime.UtcNow - cached.ObtainedUtc < TokenLifetime &&
            !string.IsNullOrWhiteSpace(cached.Token))
        {
            return cached.Token;
        }

        if (string.IsNullOrWhiteSpace(account.Password))
            throw new InvalidOperationException($"No password available for Mail.tm account '{account.EmailAddress}'.");

        var entry = await ObtainTokenAsync(client, account.EmailAddress, account.Password, cancellationToken);
        TokensByAccountId[account.Id] = entry;
        return entry.Token;
    }

    static async Task<TokenEntry> ObtainTokenAsync(HttpClient client, string email, string password, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { address = email, password });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/token")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(resp.StatusCode == HttpStatusCode.Unauthorized
                ? $"Mail.tm authentication failed for '{email}' (invalid credentials)."
                : $"Mail.tm token request failed ({(int)resp.StatusCode}): {ExtractErrorMessage(err)}");
        }

        string json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        string token = ReadString(doc.RootElement, "token");
        string remoteId = ReadString(doc.RootElement, "id");

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Mail.tm returned an empty authentication token.");

        return new TokenEntry(token, remoteId, DateTime.UtcNow);
    }

    public static string GenerateSecurePassword(int length = 20)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*";
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    public static string GenerateRandomUsername()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        char[] buf = new char[10];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(buf);
    }

    static string SanitizeUsername(string raw)
    {
        string trimmed = raw.Trim();
        int at = trimmed.IndexOf('@');
        if (at >= 0) trimmed = trimmed[..at];
        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_')
                sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : GenerateRandomUsername();
    }

    static IEnumerable<JsonElement> EnumerateCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("hydra:member", out var hydra) && hydra.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in hydra.EnumerateArray())
                    yield return item;
            }
            else if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                    yield return item;
            }
        }
    }

    static string ReadString(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? string.Empty;
        return string.Empty;
    }

    static string ExtractErrorMessage(string jsonOrText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOrText);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? jsonOrText;
            if (doc.RootElement.TryGetProperty("hydra:description", out var hd) && hd.ValueKind == JsonValueKind.String)
                return hd.GetString() ?? jsonOrText;
        }
        catch { }
        return jsonOrText;
    }
}
