using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using AnonymMailClient.Models;
using AnonymMailClient.Tor;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AnonymMailClient.Services;

public static class DirectMxSmtpService
{
    const bool IsLogEnabled = true;
    const int SmtpMxPort = 25;
    const int MaxCircuitRotationsPerDomain = 8;

    static readonly string[] DohEndpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve"
    ];

    public static string SanitizeMessageForAnonymity(MailAccount account, MimeMessage message)
    {
        if (message.From.Count == 0)
            message.From.Add(new MailboxAddress(account.Username, account.EmailAddress));

        string senderEmail = message.From.Mailboxes.FirstOrDefault()?.Address ?? account.EmailAddress;
        string senderDomain = ExtractDomain(senderEmail, account.Domain);

        message.Date = new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero);
        message.MessageId = $"{Guid.NewGuid():N}@{senderDomain}";

        message.Headers.RemoveAll(HeaderId.XMailer);
        message.Headers.RemoveAll(HeaderId.UserAgent);
        message.Headers.RemoveAll("X-Originating-IP");

        return senderDomain;
    }

    public static async Task SendAsync(
        MailAccount account,
        string toAddress,
        string subject,
        string body,
        bool isHtml = false,
        string? attachmentPath = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(toAddress))
            throw new ArgumentException("Recipient address is required.", nameof(toAddress));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.Username, account.EmailAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject ?? string.Empty;

        var builder = new BodyBuilder();
        if (isHtml)
            builder.HtmlBody = body;
        else
            builder.TextBody = body;

        if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
            builder.Attachments.Add(attachmentPath);

        message.Body = builder.ToMessageBody();

        string senderDomain = SanitizeMessageForAnonymity(account, message);
        var senderMailbox = message.From.Mailboxes.First();
        var recipientMailbox = message.To.Mailboxes.First();
        string recipientDomain = ExtractDomain(recipientMailbox.Address, string.Empty);

        onProgress?.Invoke($"Resolving MX for '{recipientDomain}' over Tor DoH...");
        var mxHosts = await ResolveMxOverTorAsync(account.Id, recipientDomain, cancellationToken);
        onProgress?.Invoke($"Found MX host(s): {string.Join(", ", mxHosts)}");

        await DeliverToDomainOverTorAsync(account, senderDomain, senderMailbox, [recipientMailbox], mxHosts, message, onProgress, cancellationToken);
        onProgress?.Invoke($"Mail sent successfully in {sw.ElapsedMilliseconds}ms.");
    }

    public static async Task<IReadOnlyList<string>> ResolveMxOverTorAsync(string isolationKey, string domain, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var client = TorService.CreateIsolatedHttpClient(isolationKey, timeout: TimeSpan.FromSeconds(30));

        foreach (string endpoint in DohEndpoints)
        {
            try
            {
                string url = $"{endpoint}?name={Uri.EscapeDataString(domain)}&type=MX";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

                using var resp = await client.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                    continue;

                string json = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                    continue;

                var parsed = new List<(int Preference, string Host)>();
                foreach (var ans in answers.EnumerateArray())
                {
                    if (ans.TryGetProperty("type", out var typeProp) && typeProp.TryGetInt32(out int rrType) && rrType != 15)
                        continue;

                    string? data = ans.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(data)) continue;

                    string[] parts = data.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int pref))
                    {
                        string host = parts[1].TrimEnd('.');
                        if (!string.IsNullOrWhiteSpace(host) && host != ".")
                            parsed.Add((pref, host));
                    }
                    else if (parts.Length == 1)
                    {
                        string host = parts[0].TrimEnd('.');
                        if (!string.IsNullOrWhiteSpace(host) && host != ".")
                            parsed.Add((10, host));
                    }
                }

                if (parsed.Count > 0)
                {
                    var hosts = parsed
                        .OrderBy(static x => x.Preference)
                        .Select(static x => x.Host)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    Log($"DirectMxSmtp: Resolved MX for '{domain}' over Tor in {sw.ElapsedMilliseconds}ms: [{string.Join(", ", hosts)}]", isRun: IsLogEnabled);
                    return hosts;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"DirectMxSmtp: DoH MX lookup via '{endpoint}' failed: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
            }
        }

        Log($"DirectMxSmtp: Found no MX records for '{domain}'; falling back to A/AAAA host '{domain}'.", LogLevel.Warning, isRun: IsLogEnabled);
        return [domain];
    }

    static async Task DeliverToDomainOverTorAsync(
        MailAccount account,
        string senderDomain,
        MailboxAddress senderMailbox,
        IReadOnlyList<MailboxAddress> recipients,
        IReadOnlyList<string> mxHosts,
        MimeMessage message,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var attemptErrors = new List<string>();

        for (int rotation = 0; rotation < MaxCircuitRotationsPerDomain; rotation++)
        {
            if (rotation > 0)
            {
                onProgress?.Invoke($"Tor circuit exit blocked port 25. Rotating circuit (attempt {rotation + 1}/{MaxCircuitRotationsPerDomain})...");
                TorService.RotateCircuit(account.Id);
            }

            var endpoint = await TorService.RequireEndpointAsync(cancellationToken);
            int epoch = TorService.CircuitEpochOf(account.Id);

            foreach (string mxHost in mxHosts)
            {
                bool exitNodeBlockedPort25 = false;

                foreach (var tlsMode in new[] { SecureSocketOptions.StartTlsWhenAvailable, SecureSocketOptions.None })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attemptSw = Stopwatch.StartNew();

                    using var smtp = new SmtpClient
                    {
                        Timeout = 35_000,
                        CheckCertificateRevocation = false,
                        ServerCertificateValidationCallback = static (_, _, _, _) => true,
                        LocalDomain = senderDomain,
                        ProxyClient = TorService.CreateProxy(endpoint, account.Id)
                    };

                    using var stepBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stepBudget.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        onProgress?.Invoke($"Connecting to {mxHost}:25 over Tor ({tlsMode})...");
                        await smtp.ConnectAsync(mxHost, SmtpMxPort, tlsMode, stepBudget.Token);

                        onProgress?.Invoke($"Delivering message to {mxHost}...");
                        await smtp.SendAsync(message, senderMailbox, recipients, stepBudget.Token);
                        await smtp.DisconnectAsync(true, stepBudget.Token);

                        Log($"DirectMxSmtp: Delivered to {mxHost}:25 over Tor ({tlsMode}) in {attemptSw.ElapsedMilliseconds}ms.", isRun: IsLogEnabled);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (SocksReplyException socksEx) when (socksEx.ReplyCode == 0x02)
                    {
                        attemptErrors.Add($"{mxHost} (circuit {epoch}): {socksEx.Message}");
                        exitNodeBlockedPort25 = true;
                        break;
                    }
                    catch (Exception ex) when (tlsMode == SecureSocketOptions.StartTlsWhenAvailable &&
                                               ex is not (SocksReplyException or SocksProtocolException or SocketException or TimeoutException or OperationCanceledException))
                    {
                        attemptErrors.Add($"{mxHost} (STARTTLS): {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        attemptErrors.Add($"{mxHost}: {ex.Message}");
                        break;
                    }
                }

                if (exitNodeBlockedPort25)
                    break;
            }
        }

        string error = $"Failed to deliver to MX host(s) on port 25 over Tor after {MaxCircuitRotationsPerDomain} circuits. Details: {string.Join(" | ", attemptErrors.TakeLast(3))}";
        Log(error, LogLevel.Error, isRun: IsLogEnabled);
        throw new InvalidOperationException(error);
    }

    static string ExtractDomain(string email, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            int at = email.LastIndexOf('@');
            if (at >= 0 && at + 1 < email.Length)
                return email[(at + 1)..].Trim().ToLowerInvariant();
        }
        return fallback;
    }
}
