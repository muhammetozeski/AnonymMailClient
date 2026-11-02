namespace AnonymMailClient.Tor;

public static partial class TorProcess
{
    #region Finding an executable

    public static string MissingExecutableAdvice =>
        "No tor executable was found. Install Tor (or Tor Browser) and, if it lives somewhere unusual, "
        + "set its path in Settings.";

    static string ExecutableName => OperatingSystem.IsWindows() ? "tor.exe" : "tor";

    static (string SettingKey, TorExecutable? Result, DateTime AtUtc)? lastSearch;
    static readonly Lock searchGate = new();
    static readonly TimeSpan MissingRecheckInterval = TimeSpan.FromSeconds(30);

    public static TorExecutable? Find()
    {
        string settingKey = Settings.TorExecutablePath.Value;
        lock (searchGate)
        {
            if (lastSearch is { } cached && cached.SettingKey == settingKey && IsStillGood(cached))
                return cached.Result;

            var found = Search();
            lastSearch = (settingKey, found, DateTime.UtcNow);
            return found;
        }
    }

    static bool IsStillGood((string SettingKey, TorExecutable? Result, DateTime AtUtc) cached) =>
        cached.Result != null
            ? File.Exists(cached.Result.Path)
            : DateTime.UtcNow - cached.AtUtc < MissingRecheckInterval;

    static TorExecutable? Search()
    {
        foreach (var (candidate, foundBy) in Candidates())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                string resolved = ResolveFinalExecutablePath(ResolveScoopShim(candidate));
                Log($"Tor executable found via {foundBy}: {resolved}", isRun: IsLogEnabled);
                return new TorExecutable(resolved, foundBy);
            }
            catch (Exception ex)
            {
                Log($"Tor executable candidate '{candidate}' could not be checked: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
            }
        }
        Log("No tor executable was found on this machine.", LogLevel.Warning, isRun: IsLogEnabled);
        return null;
    }

    static IEnumerable<(string Path, string FoundBy)> Candidates()
    {
        if (Settings.TorExecutablePath.Value is { Length: > 0 } configured)
            yield return (configured.Trim('"'), "Settings.TorExecutablePath");

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(directory, ExecutableName); }
            catch { continue; }
            yield return (candidate, "PATH");
        }

        yield return (Path.Combine(AppPaths.Root, "Tor", ExecutableName), "the application folder");

        if (!OperatingSystem.IsWindows()) yield break;

        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(static r => r.Length > 0).Distinct())
        {
            yield return (Path.Combine(root, "Tor Browser", "Browser", "TorBrowser", "Tor", "tor.exe"), "Tor Browser install");
            yield return (Path.Combine(root, "Programs", "Tor Browser", "Browser", "TorBrowser", "Tor", "tor.exe"), "Tor Browser install");
        }

        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tor", "tor.exe"), "Tor Expert Bundle");
    }

    static string ResolveScoopShim(string candidate)
    {
        string shim = Path.ChangeExtension(candidate, ".shim");
        if (!File.Exists(shim)) return candidate;

        foreach (string line in File.ReadAllLines(shim))
        {
            if (!line.TrimStart().StartsWith("path", StringComparison.OrdinalIgnoreCase)) continue;
            int separator = line.IndexOf('=');
            if (separator < 0) continue;

            string target = line[(separator + 1)..].Trim().Trim('"');
            if (File.Exists(target))
            {
                Log($"Followed Scoop shim '{shim}' to '{target}'.", isRun: IsLogEnabled);
                return target;
            }
        }
        return candidate;
    }

    static string ResolveFinalExecutablePath(string candidate)
    {
        try
        {
            string full = Path.GetFullPath(candidate);
            var fileInfo = new FileInfo(full);
            if (fileInfo.LinkTarget != null && fileInfo.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true } targetFile)
                full = targetFile.FullName;

            var currentDir = new DirectoryInfo(Path.GetDirectoryName(full)!);
            var relativeParts = new Stack<string>();
            relativeParts.Push(Path.GetFileName(full));

            while (currentDir != null)
            {
                if (currentDir.LinkTarget != null && currentDir.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true } targetDir)
                {
                    string finalPath = targetDir.FullName;
                    while (relativeParts.Count > 0)
                        finalPath = Path.Combine(finalPath, relativeParts.Pop());

                    if (File.Exists(finalPath))
                        return Path.GetFullPath(finalPath);
                }

                relativeParts.Push(currentDir.Name);
                currentDir = currentDir.Parent;
            }

            return full;
        }
        catch
        {
            return candidate;
        }
    }

    #endregion
}
