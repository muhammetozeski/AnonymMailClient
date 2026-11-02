using System.Text.Json;
using AnonymMailClient.Models;

namespace AnonymMailClient.Storage;

public static class MessageCacheStore
{
    const bool IsLogEnabled = true;

    static readonly string BaseFolder = Path.Combine(AppPaths.UserData, "Messages");
    static readonly Lock fileGate = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    static string GetAccountFilePath(string accountId)
    {
        return Path.Combine(BaseFolder, $"{accountId}.json");
    }

    public static List<MailMessageSummary> LoadMessages(string accountId)
    {
        lock (fileGate)
        {
            try
            {
                string path = GetAccountFilePath(accountId);
                if (!File.Exists(path))
                    return [];

                string json = File.ReadAllText(path);
                var messages = JsonSerializer.Deserialize<List<MailMessageSummary>>(json, JsonOptions);
                return messages ?? [];
            }
            catch (Exception ex)
            {
                Log($"MessageCacheStore: Failed to load messages for account '{accountId}': {ex.Message}", LogLevel.Error, isRun: IsLogEnabled);
                return [];
            }
        }
    }

    public static void SaveMessages(string accountId, IEnumerable<MailMessageSummary> messages)
    {
        lock (fileGate)
        {
            try
            {
                Directory.CreateDirectory(BaseFolder);
                string path = GetAccountFilePath(accountId);
                string json = JsonSerializer.Serialize(messages.ToList(), JsonOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Log($"MessageCacheStore: Failed to save messages for account '{accountId}': {ex.Message}", LogLevel.Error, isRun: IsLogEnabled);
            }
        }
    }

    public static void UpsertMessage(string accountId, MailMessageSummary message)
    {
        var list = LoadMessages(accountId);
        int idx = list.FindIndex(m => string.Equals(m.Id, message.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            // Preserve body if updated item didn't fetch it yet
            if (string.IsNullOrEmpty(message.BodyText) && !string.IsNullOrEmpty(list[idx].BodyText))
                message.BodyText = list[idx].BodyText;
            if (string.IsNullOrEmpty(message.BodyHtml) && !string.IsNullOrEmpty(list[idx].BodyHtml))
                message.BodyHtml = list[idx].BodyHtml;

            list[idx] = message;
        }
        else
        {
            list.Insert(0, message);
        }

        SaveMessages(accountId, list);
    }

    public static void MarkAsRead(string accountId, string messageId)
    {
        var list = LoadMessages(accountId);
        var msg = list.FirstOrDefault(m => string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));
        if (msg != null && !msg.IsRead)
        {
            msg.IsRead = true;
            SaveMessages(accountId, list);
        }
    }
}
