using System.Text.Json;
using AnonymMailClient.Models;

namespace AnonymMailClient.Storage;

public static class AccountStore
{
    const bool IsLogEnabled = true;

    static readonly string FilePath = Path.Combine(AppPaths.UserData, "accounts.json");
    static readonly Lock fileGate = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static List<MailAccount> LoadAccounts()
    {
        lock (fileGate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return [];

                string json = File.ReadAllText(FilePath);
                var accounts = JsonSerializer.Deserialize<List<MailAccount>>(json, JsonOptions);
                return accounts ?? [];
            }
            catch (Exception ex)
            {
                Log($"AccountStore: Failed to load accounts from '{FilePath}': {ex.Message}", LogLevel.Error, isRun: IsLogEnabled);
                return [];
            }
        }
    }

    public static void SaveAccounts(IEnumerable<MailAccount> accounts)
    {
        lock (fileGate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.UserData);
                string json = JsonSerializer.Serialize(accounts.ToList(), JsonOptions);
                File.WriteAllText(FilePath, json);
                Log($"AccountStore: Saved accounts to '{FilePath}'.", isRun: IsLogEnabled);
            }
            catch (Exception ex)
            {
                Log($"AccountStore: Failed to save accounts to '{FilePath}': {ex.Message}", LogLevel.Error, isRun: IsLogEnabled);
            }
        }
    }

    public static void SaveOrUpdateAccount(MailAccount account)
    {
        var accounts = LoadAccounts();
        int index = accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            accounts[index] = account;
        else
            accounts.Add(account);

        SaveAccounts(accounts);
    }

    public static void RemoveAccount(string accountId)
    {
        var accounts = LoadAccounts();
        accounts.RemoveAll(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        SaveAccounts(accounts);
    }
}
