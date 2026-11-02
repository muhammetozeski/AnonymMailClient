using AnonymMailClient.Services;
using AnonymMailClient.Tor;
using AnonymMailClient.UI;

namespace AnonymMailClient;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--test-e2e", StringComparer.OrdinalIgnoreCase))
        {
            return RunE2ETestAsync().GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();

        var initResult = ApplicationServices.Initialize();
        if (!initResult.Succeeded)
        {
            Log($"Application initialization warning: {initResult.Error?.Message}", LogLevel.Warning);
        }

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Log($"Unhandled application exception: {ex}", LogLevel.Critical);
            MessageBox.Show(ex.ToString(), "AnonymMailClient Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return 0;
    }

    static async Task<int> RunE2ETestAsync()
    {
        Console.WriteLine("=== AnonymMailClient E2E Tor Test ===");
        try
        {
            Console.WriteLine("[1/5] Bootstrapping Tor...");
            var endpoint = await TorService.RequireEndpointAsync(CancellationToken.None);
            Console.WriteLine($"[1/5] Tor Ready: {endpoint}");

            Console.WriteLine("[2/5] Provisioning Guerrilla Mail account over Tor...");
            string testUser = "tst" + Random.Shared.Next(100000, 999999);
            var acc = await GuerrillaMailService.CreateCustomAccountAsync(testUser, "sharklasers.com");
            Console.WriteLine($"[2/5] Guerrilla Account Created: {acc.EmailAddress}");

            Console.WriteLine("[3/5] Provisioning Mail.tm account over Tor...");
            var mailTmAcc = await MailTmService.CreateAccountAsync(null, null);
            Console.WriteLine($"[3/5] Mail.tm Account Created: {mailTmAcc.EmailAddress}");

            Console.WriteLine("[4/5] Checking inboxes over Tor...");
            var gmMsgs = await MailAccountService.CheckMessagesAsync(acc);
            var tmMsgs = await MailAccountService.CheckMessagesAsync(mailTmAcc);
            Console.WriteLine($"[4/5] Inboxes checked: GM={gmMsgs.Count}, TM={tmMsgs.Count}");

            Console.WriteLine("[5/5] Sending Direct-to-MX email over Tor to Mail.tm...");
            await DirectMxSmtpService.SendAsync(
                acc,
                mailTmAcc.EmailAddress,
                "AnonymMailClient E2E Test",
                "Automated Tor delivery test to Mail.tm.",
                isHtml: false,
                onProgress: msg => Console.WriteLine($"   > {msg}"));
            Console.WriteLine("[5/5] Direct-to-MX delivery over Tor succeeded!");

            Console.WriteLine("=== ALL TESTS PASSED SUCCESSFULLY! ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TEST FAILED: {ex}");
            return 1;
        }
        finally
        {
            TorProcess.Stop();
        }
    }
}
