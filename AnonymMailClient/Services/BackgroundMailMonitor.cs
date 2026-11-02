using AnonymMailClient.Models;
using AnonymMailClient.Storage;

namespace AnonymMailClient.Services;

public static class BackgroundMailMonitor
{
    const bool IsLogEnabled = true;

    static System.Threading.Timer? timer;
    static readonly SemaphoreSlim checkGate = new(1, 1);
    static bool isChecking;

    public static event Action<MailAccount, IReadOnlyList<MailMessageSummary>>? NewMailReceived;
    public static event Action? StatusChanged;
    public static event Action? AccountsUpdated;

    public static bool IsChecking => isChecking;
    public static DateTime? LastCheckUtc { get; private set; }

    public static void Start()
    {
        int minutes = Math.Max(1, Settings.CheckIntervalMinutes.Value);
        Log($"BackgroundMailMonitor: Starting timer with interval {minutes} minute(s).", isRun: IsLogEnabled);

        timer?.Dispose();
        timer = new System.Threading.Timer(
            static _ => _ = RunCheckSafeAsync(),
            null,
            TimeSpan.FromSeconds(15), // Initial check after 15s
            TimeSpan.FromMinutes(minutes));
    }

    public static void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public static Task CheckAllNowAsync()
    {
        return RunCheckSafeAsync();
    }

    static async Task RunCheckSafeAsync()
    {
        if (!await checkGate.WaitAsync(0))
            return;

        isChecking = true;
        StatusChanged?.Invoke();

        try
        {
            var accounts = AccountStore.LoadAccounts();
            if (accounts.Count == 0)
                return;

            Log($"BackgroundMailMonitor: Checking {accounts.Count} account(s)...", isRun: IsLogEnabled);

            foreach (var account in accounts)
            {
                try
                {
                    var existing = MessageCacheStore.LoadMessages(account.Id);
                    var existingIds = existing.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var remoteItems = await GuerrillaMailService.CheckMessagesAsync(account);
                    var newItems = new List<MailMessageSummary>();

                    foreach (var item in remoteItems)
                    {
                        if (!existingIds.Contains(item.Id))
                        {
                            newItems.Add(item);
                            MessageCacheStore.UpsertMessage(account.Id, item);
                        }
                    }

                    account.UnreadCount = MessageCacheStore.LoadMessages(account.Id).Count(m => !m.IsRead);
                    account.LastCheckedUtc = DateTime.UtcNow;
                    AccountStore.SaveOrUpdateAccount(account);

                    if (newItems.Count > 0)
                    {
                        Log($"BackgroundMailMonitor: Account '{account.EmailAddress}' received {newItems.Count} new message(s)!", isRun: IsLogEnabled);
                        NewMailReceived?.Invoke(account, newItems);
                    }
                }
                catch (Exception ex)
                {
                    Log($"BackgroundMailMonitor: Failed to check '{account.EmailAddress}': {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
                }
            }

            LastCheckUtc = DateTime.UtcNow;
            AccountsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"BackgroundMailMonitor: Global check error: {ex.Message}", LogLevel.Error, isRun: IsLogEnabled);
        }
        finally
        {
            isChecking = false;
            StatusChanged?.Invoke();
            checkGate.Release();
        }
    }
}
