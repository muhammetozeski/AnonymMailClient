using System.Diagnostics;

namespace AnonymMailClient.Tor;

public sealed record TorBridge(string Line, string? Transport);

public static class TorBridges
{
    public const char LineSeparator = ' ';

    static readonly Dictionary<string, string[]> PluginsByTransport = new(StringComparer.OrdinalIgnoreCase)
    {
        ["obfs4"] = ["lyrebird", "obfs4proxy"],
        ["meek_lite"] = ["lyrebird", "obfs4proxy"],
        ["webtunnel"] = ["lyrebird"],
        ["scramblesuit"] = ["lyrebird", "obfs4proxy"],
        ["conjure"] = ["conjure-client"],
        ["snowflake"] = ["snowflake-client"]
    };

    static readonly string[] PluginFolderNames = ["pluggable_transports", "PluggableTransports"];

    public static IReadOnlyList<TorBridge> Configured =>
    [
        .. Settings.TorBridgeLines.Value
            .Split([LineSeparator, '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.StartsWith('#'))
            .Select(static line => new TorBridge(line, TransportOf(line)))
    ];

    internal static string? TransportOf(string line)
    {
        string first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return first.Length == 0 || first.Contains(':') ? null : first;
    }

    public static string RequirePlugin(string transport, string torExecutablePath)
    {
        var searched = new List<string>();
        string? torFolder = Path.GetDirectoryName(torExecutablePath);

        if (PluginsByTransport.TryGetValue(transport, out string[]? names))
        {
            foreach (string baseFolder in Roots(torFolder))
                foreach (string folderName in PluginFolderNames)
                    foreach (string name in names)
                    {
                        string candidate = Path.Combine(baseFolder, folderName, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
                        searched.Add(candidate);
                        if (File.Exists(candidate)) return candidate;
                    }
        }

        throw new TorUnavailableException(
            $"The bridge transport '{transport}' needs a pluggable-transport program and none was found. Looked in: "
            + string.Join("; ", searched.Select(Path.GetDirectoryName).Distinct()),
            recoverable: false);
    }

    static IEnumerable<string> Roots(string? torFolder)
    {
        if (string.IsNullOrEmpty(torFolder)) yield break;
        yield return torFolder;
        if (Path.GetDirectoryName(torFolder) is { Length: > 0 } parent) yield return parent;
    }

    public static void AddTo(ProcessStartInfo startInfo, string torExecutablePath)
    {
        var bridges = Configured;
        if (bridges.Count == 0) return;

        startInfo.ArgumentList.Add("--UseBridges");
        startInfo.ArgumentList.Add("1");

        foreach (string transport in bridges.Select(static b => b.Transport)
                     .Where(static t => t is { Length: > 0 })
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Cast<string>())
        {
            startInfo.ArgumentList.Add("--ClientTransportPlugin");
            startInfo.ArgumentList.Add($"{transport} exec {RequirePlugin(transport, torExecutablePath)}");
        }

        foreach (var bridge in bridges)
        {
            startInfo.ArgumentList.Add("--Bridge");
            startInfo.ArgumentList.Add(bridge.Line);
        }
    }
}
