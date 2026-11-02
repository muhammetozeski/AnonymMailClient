using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AnonymMailClient;

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public static class Logger
{
    internal const bool IsLogEnabled = true;
    private static int _activateLogging = 1;

    public static bool ActivateLogging
    {
        get => Interlocked.CompareExchange(ref _activateLogging, 0, 0) == 1;
        set => Interlocked.Exchange(ref _activateLogging, value ? 1 : 0);
    }

    const bool PrintDebugModStyle = false;

    public static readonly string startTime = DateTime.Now.ToString("yyyy.MM.dd HH.mm.ss.ff");
    public static readonly string LogsFolder = Path.Combine(AppPaths.AppCache, "Logs");
    public const string LogFileNamePrefix = "Logs";
    public readonly static string LogFileName = Path.Combine(LogsFolder, $"{LogFileNamePrefix} {startTime}.txt");

    const int DeleteOlderThanLastXFile = 3;

    public static readonly ConcurrentQueue<string> AllLogs = new();
    public static readonly ConcurrentQueue<string?> AllLogsUserFriendly = new();
    private static readonly BlockingCollection<string> _logQueue = [];

    static Logger()
    {
        if (!ActivateLogging)
            return;

        if (!string.IsNullOrWhiteSpace(LogsFolder))
        {
            Directory.CreateDirectory(LogsFolder);
        }

        if (DeleteOlderThanLastXFile > -1)
            try
            {
                DeleteOldestFiles(LogsFolder, DeleteOlderThanLastXFile, LogFileNamePrefix);
            }
            catch (Exception e)
            {
                Log("An Error occured while deleting the old log files: " + e);
            }

        new Thread(() =>
        {
            foreach (string message in _logQueue.GetConsumingEnumerable())
            {
                File.AppendAllText(LogFileName, message + "\n");
            }
        })
        { IsBackground = true }.Start();
    }

    public static string? Log(object? message, LogLevel level, bool isRun = true, [CallerMemberName] string callerFunction = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLine = 0)
    {
        ConsoleColor color = level switch
        {
            LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.White
        };
        return Log($"[{level}] {message}", consoleColor: color, isRun: isRun, callerFunction: callerFunction, callerFilePath: callerFilePath, callerLine: callerLine);
    }

    public static string? Log(object? MessageObject, ConsoleColor? consoleColor = null, bool PrintToConsole = true, bool WriteToDisk = true, bool UseNewLine = true,
        [CallerMemberName] string callerFunction = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLine = 0, bool isRun = true
        )
    {
        if (!ActivateLogging)
        {
            if (PrintToConsole)
            {
                var defaultColor = Console.ForegroundColor;
                if (consoleColor != null)
                {
                    Console.ForegroundColor = consoleColor.Value;
                }
                if (UseNewLine)
                    Console.WriteLine(MessageObject ?? "null");
                else Console.Write(MessageObject ?? "null");
                Console.ForegroundColor = defaultColor;
            }
            return null;
        }

        if (!isRun || (!PrintToConsole && !WriteToDisk)) return null;

        string? returnValue = null;

        const string prefix = "> ";
        const string suffix = "\n-----------------------------\n\n";
        string CallerFile;
        try
        {
            CallerFile = Path.GetFileName(callerFilePath);
            if (string.IsNullOrWhiteSpace(CallerFile))
            {
                CallerFile = "null";
            }
        }
        catch (Exception e)
        {
            CallerFile = "\"exception: " + e.Message + "\"";
        }

        string now = DateTime.UtcNow.AddHours(3).ToString();

        string Message = prefix + "[" + now + "] " +
            "[" + CallerFile + "/" + callerFunction + " Line: " + callerLine +
            " Thread Id: " + Environment.CurrentManagedThreadId + "]:\n";

        try
        {
            if (MessageObject is System.Collections.IEnumerable numerable and not string)
            {
                foreach (var item in numerable)
                {
                    try
                    {
                        string? itemString = item?.ToString();

                        if (returnValue == null)
                            returnValue = itemString ?? "null";
                        else
                            returnValue += itemString ?? "null";

                        returnValue += "\n";
                        Message += itemString ?? "item.ToString() in given numerable, has returned null\n";
                    }
                    catch (Exception e)
                    {
                        returnValue += "error";
                        Message += "An error occured while converting to string the given object in the list in Log():\n" + e + "\n";
                    }
                }
            }
            else
            {
                returnValue = MessageObject?.ToString();
                Message += returnValue ?? "MessageObject.ToString() has returned null";
            }
        }
        catch (Exception e)
        {
            Message += "An error occured while converting to string the given object in Log():\n" + e;
        }

        Message += suffix;
        if (PrintToConsole)
        {
            var defaultColor = Console.ForegroundColor;
            if (consoleColor != null)
            {
                Console.ForegroundColor = consoleColor.Value;
            }

#pragma warning disable CS0162
            if (PrintDebugModStyle)
            {
                if (UseNewLine) Console.WriteLine(Message);
                else Console.Write(Message);
            }
            else
            {
                if (UseNewLine) Console.WriteLine(MessageObject);
                else Console.Write(MessageObject);
            }
#pragma warning restore CS0162

            Console.ForegroundColor = defaultColor;
        }

        AllLogs.Enqueue(Message);
        AllLogsUserFriendly.Enqueue(returnValue);

        if (WriteToDisk)
            _logQueue.Add(Message);

        return returnValue;
    }
}
