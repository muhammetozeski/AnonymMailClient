using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnonymMailClient.Tor;

public sealed record TorExecutable(string Path, string FoundBy);

public static partial class TorProcess
{
    const bool IsLogEnabled = true;

    public static string DataDirectory => Path.Combine(AppPaths.AppCache, "Tor");

    static string ManagedStateFilePath => Path.Combine(DataDirectory, "managed-tor.json");

    sealed record ManagedTorState(int Pid, int SocksPort, long StartTicks, string ExecutablePath);

    static Process? running;
    static readonly Lock startGate = new();
    static readonly SemaphoreSlim startAsyncGate = new(1, 1);
    static int stopGeneration;

    static readonly Queue<string> log = new();
    const int MaxLoggedLines = 200;

    public static IReadOnlyCollection<string> RecentOutput
    {
        get { lock (log) return [.. log]; }
    }

    public static bool IsRunning => running is { HasExited: false };

    static int? ownSocksPort;
    public static int? OwnSocksPort => IsRunning ? ownSocksPort : null;

    static void Remember(string line)
    {
        lock (log)
        {
            log.Enqueue(line);
            while (log.Count > MaxLoggedLines) log.Dequeue();
        }
    }

    #region Starting one

    public static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public static async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await startAsyncGate.WaitAsync(cancellationToken);
        try
        {
            int myStopGeneration;
            lock (startGate)
            {
                myStopGeneration = stopGeneration;
                if (IsRunning && OwnSocksPort is { } already)
                {
                    Log($"Tor is already running under this app on port {already}.", isRun: IsLogEnabled);
                    return already;
                }
            }

            var sw = Stopwatch.StartNew();
            var executable = Find() ?? throw new TorUnavailableException(MissingExecutableAdvice, recoverable: false);

            string activeDataDirectory = PrepareDataDirectory(executable.Path);
            int socksPort = FindFreePort();

            var startInfo = new ProcessStartInfo(executable.Path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable.Path) ?? activeDataDirectory
            };

            startInfo.ArgumentList.Add("--SocksPort");
            startInfo.ArgumentList.Add($"{socksPort} IsolateSOCKSAuth IsolateDestAddr IsolateDestPort");
            startInfo.ArgumentList.Add("--DataDirectory");
            startInfo.ArgumentList.Add(activeDataDirectory);
            startInfo.ArgumentList.Add("--ClientOnly");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--ControlPort");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("--AvoidDiskWrites");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--Log");
            startInfo.ArgumentList.Add("notice stdout");

            AddGeoIpFiles(startInfo, executable.Path);
            TorBridges.AddTo(startInfo, executable.Path);

            lock (log) log.Clear();

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var bootstrapped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = bootstrapped.Task.ContinueWith(static finished => _ = finished.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            process.OutputDataReceived += (_, e) => ReadTorLine(e.Data, bootstrapped);
            process.ErrorDataReceived += (_, e) => ReadTorLine(e.Data, bootstrapped);
            process.Exited += (_, _) =>
            {
                int exitCode = SafeExitCode(process);
                string lastLines = LastMeaningfulLine();
                Log($"Tor process (PID {SafeProcessId(process)}) exited with code {exitCode}. Output: {lastLines}", LogLevel.Warning, isRun: IsLogEnabled);
                bootstrapped.TrySetException(new TorUnavailableException(
                    $"Tor exited before it finished starting (exit code {exitCode}). Last output: {lastLines}"));
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                Log($"Failed to start tor process from '{executable.Path}':\n{ex}", LogLevel.Error, isRun: IsLogEnabled);
                process.Dispose();
                throw;
            }

            AttachToKillOnCloseJob(process);
            SaveManagedState(process, socksPort, executable.Path);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (startGate)
            {
                if (stopGeneration != myStopGeneration)
                {
                    Log("Tor was asked to stop while starting; ending process.", LogLevel.Warning, isRun: IsLogEnabled);
                    KillQuietly(process);
                    DeleteManagedState();
                    throw new TorUnavailableException("Tor was stopped while starting.", recoverable: false);
                }

                running?.Dispose();
                running = process;
                ownSocksPort = socksPort;
            }
            RegisterShutdownHook();
            Log($"Started tor (PID {SafeProcessId(process)}) from '{executable.Path}' on SOCKS port {socksPort} with DataDirectory '{activeDataDirectory}'.", isRun: IsLogEnabled);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Settings.TorStartupTimeoutSeconds.Value)));
            try
            {
                await bootstrapped.Task.WaitAsync(budget.Token);
                Log($"Tor bootstrapped in {sw.ElapsedMilliseconds} ms; SOCKS port {socksPort} ready.", isRun: IsLogEnabled);
                return socksPort;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TorUnavailableException(
                    $"Tor did not finish starting within {Settings.TorStartupTimeoutSeconds.Value}s. Last output: {LastMeaningfulLine()}");
            }
        }
        finally
        {
            startAsyncGate.Release();
        }
    }

    static string PrepareDataDirectory(string executablePath)
    {
        Directory.CreateDirectory(DataDirectory);

        try
        {
            if (File.Exists(ManagedStateFilePath))
            {
                var state = JsonSerializer.Deserialize<ManagedTorState>(File.ReadAllText(ManagedStateFilePath));
                if (state != null)
                {
                    TryKillOrphanProcessById(state.Pid, "managed-tor.json");
                }
                DeleteManagedState();
            }
        }
        catch (Exception ex)
        {
            Log($"Could not inspect managed-tor.json: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
        }

        string lockFilePath = Path.Combine(DataDirectory, "lock");
        if (IsLockFileHeld(lockFilePath))
        {
            Log($"Tor lock file '{lockFilePath}' is held by an orphaned process; cleaning up matching tor instances.", LogLevel.Warning, isRun: IsLogEnabled);
            KillOrphanTorHoldingDataDirectory(executablePath);

            var waitSw = Stopwatch.StartNew();
            while (IsLockFileHeld(lockFilePath) && waitSw.ElapsedMilliseconds < 4000)
            {
                Thread.Sleep(150);
            }
        }

        if (IsLockFileHeld(lockFilePath))
        {
            string fallbackDir = Path.Combine(DataDirectory, $"instance-{Environment.ProcessId}");
            Directory.CreateDirectory(fallbackDir);
            Log($"Tor lock file '{lockFilePath}' remained locked; using isolated directory '{fallbackDir}'.", LogLevel.Warning, isRun: IsLogEnabled);
            return fallbackDir;
        }

        return DataDirectory;
    }

    static bool IsLockFileHeld(string lockFilePath)
    {
        if (!File.Exists(lockFilePath)) return false;
        try
        {
            using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void TryKillOrphanProcessById(int pid, string reason)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;
            if (!proc.ProcessName.Contains("tor", StringComparison.OrdinalIgnoreCase)) return;

            Log($"Terminating orphaned tor process PID {pid} ({reason}).", LogLevel.Warning, isRun: IsLogEnabled);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            Log($"Could not terminate orphaned tor PID {pid}: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
        }
    }

    static void KillOrphanTorHoldingDataDirectory(string executablePath)
    {
        string normalizedExec = Path.GetFullPath(executablePath);
        string? scoopTorAppsRoot = null;
        int scoopIdx = normalizedExec.IndexOf(@"\scoop\apps\tor\", StringComparison.OrdinalIgnoreCase);
        if (scoopIdx >= 0)
            scoopTorAppsRoot = normalizedExec[..(scoopIdx + @"\scoop\apps\tor\".Length)];

        foreach (var proc in Process.GetProcessesByName("tor"))
        {
            try
            {
                if (proc.HasExited) continue;
                string? procPath = null;
                try { procPath = proc.MainModule?.FileName; } catch { }

                if (string.IsNullOrWhiteSpace(procPath)) continue;
                string fullProcPath = Path.GetFullPath(procPath);

                bool isSameTorBinary =
                    string.Equals(fullProcPath, normalizedExec, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ResolveFinalExecutablePath(fullProcPath), normalizedExec, StringComparison.OrdinalIgnoreCase) ||
                    (scoopTorAppsRoot != null && fullProcPath.StartsWith(scoopTorAppsRoot, StringComparison.OrdinalIgnoreCase));

                if (!isSameTorBinary) continue;

                Log($"Terminating orphan tor process PID {proc.Id} ('{fullProcPath}') holding '{DataDirectory}'.", LogLevel.Warning, isRun: IsLogEnabled);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log($"Could not inspect or stop tor process PID {proc.Id}: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
    }

    static void SaveManagedState(Process process, int socksPort, string executablePath)
    {
        try
        {
            long ticks = 0;
            try { ticks = process.StartTime.ToUniversalTime().Ticks; } catch { }
            var state = new ManagedTorState(SafeProcessId(process), socksPort, ticks, executablePath);
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(ManagedStateFilePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            Log($"Could not write managed-tor.json: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
        }
    }

    static void DeleteManagedState()
    {
        try
        {
            if (File.Exists(ManagedStateFilePath))
                File.Delete(ManagedStateFilePath);
        }
        catch { }
    }

    static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    static string LastMeaningfulLine()
    {
        var lines = RecentOutput;
        if (lines.Count == 0) return "(tor printed nothing)";

        var important = lines
            .Where(static l => l.Contains("[warn]", StringComparison.OrdinalIgnoreCase) || l.Contains("[err]", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4)
            .ToList();

        if (important.Count > 0)
            return string.Join(" | ", important);

        return string.Join(" | ", lines.TakeLast(3));
    }

    static void AddGeoIpFiles(ProcessStartInfo startInfo, string executablePath)
    {
        string? folder = Path.GetDirectoryName(executablePath);
        if (folder == null) return;

        foreach (var (option, fileName) in new[] { ("--GeoIPFile", "geoip"), ("--GeoIPv6File", "geoip6") })
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(folder, fileName),
                         Path.Combine(folder, "data", fileName),
                         Path.Combine(folder, "..", "data", fileName),
                         Path.Combine(folder, "..", "data.original", fileName),
                         Path.Combine(folder, "..", "Data", "Tor", fileName)
                     })
            {
                if (!File.Exists(candidate)) continue;
                startInfo.ArgumentList.Add(option);
                startInfo.ArgumentList.Add(Path.GetFullPath(candidate));
                break;
            }
        }
    }

    public sealed record BootstrapState(int Percent, string Phase, string Summary, string? Problem, DateTime AtUtc);
    public static BootstrapState? Bootstrap { get; private set; }

    [GeneratedRegex(@"Bootstrapped (\d+)%\s*\(([^)]*)\):\s*(.*)", RegexOptions.IgnoreCase)]
    private static partial Regex BootstrapProgress();

    [GeneratedRegex(@"Problem bootstrapping.*?Stuck at (\d+)%(?:\s*\(([^)]*)\))?:\s*(.*)", RegexOptions.IgnoreCase)]
    private static partial Regex BootstrapStuck();

    static void ReadTorLine(string? line, TaskCompletionSource<bool> bootstrapped)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Remember(line);
        TrackBootstrap(line);

        if (line.Contains("Bootstrapped 100%", StringComparison.OrdinalIgnoreCase))
        {
            Log("Tor: " + line, isRun: IsLogEnabled);
            bootstrapped.TrySetResult(true);
        }
        else if (line.Contains("[err]", StringComparison.OrdinalIgnoreCase))
        {
            Log("Tor: " + line, LogLevel.Error, isRun: IsLogEnabled);
        }
        else if (line.Contains("Bootstrapped", StringComparison.OrdinalIgnoreCase) || line.Contains("[warn]", StringComparison.OrdinalIgnoreCase))
        {
            Log("Tor: " + line, LogLevel.Warning, isRun: IsLogEnabled);
        }
    }

    static void TrackBootstrap(string line)
    {
        if (BootstrapProgress().Match(line) is { Success: true } progress
            && int.TryParse(progress.Groups[1].Value, out int percent))
        {
            Bootstrap = new BootstrapState(percent, progress.Groups[2].Value.Trim(), progress.Groups[3].Value.Trim(), null, DateTime.UtcNow);
            return;
        }

        if (BootstrapStuck().Match(line) is { Success: true } stuck
            && int.TryParse(stuck.Groups[1].Value, out int stuckPercent))
        {
            string phase = stuck.Groups[2].Success ? stuck.Groups[2].Value.Trim() : Bootstrap?.Phase ?? "stuck";
            Bootstrap = new BootstrapState(stuckPercent, phase,
                Bootstrap?.Summary ?? string.Empty, stuck.Groups[3].Value.Trim(), DateTime.UtcNow);
        }
    }

    static bool shutdownHookRegistered;

    static void RegisterShutdownHook()
    {
        lock (startGate)
        {
            if (shutdownHookRegistered) return;
            shutdownHookRegistered = true;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    public static void Stop()
    {
        Process? process;
        lock (startGate)
        {
            stopGeneration++;
            process = running;
            running = null;
            ownSocksPort = null;
        }
        DeleteManagedState();
        if (process == null) return;

        KillQuietly(process);
        Log("The tor process this app started was stopped.", isRun: IsLogEnabled);
    }

    static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not stop tor process: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    #endregion

    #region Windows Job Object

    static IntPtr windowsJobHandle;
    static readonly Lock jobGate = new();

    static void AttachToKillOnCloseJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            lock (jobGate)
            {
                if (windowsJobHandle == IntPtr.Zero)
                {
                    windowsJobHandle = CreateJobObjectW(IntPtr.Zero, null);
                    if (windowsJobHandle != IntPtr.Zero)
                    {
                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                        {
                            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                            {
                                LimitFlags = 0x00002000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                            }
                        };
                        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                        if (!SetInformationJobObject(windowsJobHandle, 9, ref info, (uint)size))
                        {
                            CloseHandle(windowsJobHandle);
                            windowsJobHandle = IntPtr.Zero;
                        }
                    }
                }

                if (windowsJobHandle != IntPtr.Zero)
                {
                    AssignProcessToJobObject(windowsJobHandle, process.Handle);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Could not attach tor process to Windows Job Object: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    #endregion
}
