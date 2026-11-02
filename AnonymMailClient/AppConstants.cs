namespace AnonymMailClient;

/// <summary>Contains application identity and file-format values shared by all features.</summary>
internal static class AppConstants
{
    internal const bool IsLogEnabled = true;

    public const string AppTitle = "AnonymMailClient";
    public const string AppVersion = "1.0.0";
    public const string ConfigFileName = "settings.txt";

    public static readonly string ThisExePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
    public static readonly string ThisExeFolder = Path.GetDirectoryName(ThisExePath) ?? AppContext.BaseDirectory;
}
