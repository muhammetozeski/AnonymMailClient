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

        Application.Run(new MainForm());
        return 0;
    }

    static async Task<int> RunE2ETestAsync()
    {
        Console.WriteLine("=== AnonymMailClient E2E Tor Test ===");
        try
        {
            Console.WriteLine("[1/4] Bootstrapping Tor...");
            var endpoint = await TorService.RequireEndpointAsync(CancellationToken.None);
            Console.WriteLine($"[1/4] Tor Ready: {endpoint}");

            Console.WriteLine("[2/4] Provisioning Guerrilla Mail account over Tor...");
            string testUser = "tst" + Random.Shared.Next(100000, 999999);
            var acc = await GuerrillaMailService.CreateCustomAccountAsync(testUser, "sharklasers.com");
            Console.WriteLine($"[2/4] Account Created: {acc.EmailAddress} (sid: {acc.SidToken})");

            Console.WriteLine("[3/4] Checking inbox over Tor...");
            var msgs = await GuerrillaMailService.CheckMessagesAsync(acc);
            Console.WriteLine($"[3/4] Inbox checked: {msgs.Count} message(s) found.");

            Console.WriteLine("[4/4] Sending Direct-to-MX email over Tor to Guerrilla Mail...");
            await DirectMxSmtpService.SendAsync(
                acc,
                acc.EmailAddress,
                "AnonymMailClient Self-Test",
                "This is an automated E2E test verifying Direct-to-MX delivery over Tor.",
                isHtml: false,
                onProgress: msg => Console.WriteLine($"   > {msg}"));
            Console.WriteLine("[4/4] Direct-to-MX delivery over Tor succeeded!");

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
