using System.Diagnostics;
using System.Runtime.InteropServices;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Manages launching copilot as independent/detached processes and verifying liveness.
/// Tracks PID + start time to detect PID reuse across daemon restarts.
/// </summary>
public sealed partial class ProcessManager
{
    private static readonly TimeSpan SignalDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<ProcessManager> _logger;

    public ProcessManager(ILogger<ProcessManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Launches a copilot process detached from this daemon so it survives daemon crashes.
    /// Returns the populated session on success, or null on failure.
    /// </summary>
    public DispatchSession? LaunchCopilot(DispatchSession session, CopilotdConfig config, GitHubIssue issue)
    {
        var repoPath = Path.Combine(config.RepoHome ?? ".", issue.Repo);
        if (!Directory.Exists(repoPath))
        {
            _logger.LogWarning("Repo directory not found: {Path}", repoPath);
            return null;
        }

        var prompt = BuildPrompt(config, issue, session);
        var args = BuildArguments(session, prompt, config.Rules.GetValueOrDefault(session.RuleName));

        _logger.LogInformation("Launching copilot for {IssueKey} with session {SessionId}", session.IssueKey, session.CopilotSessionId);
        _logger.LogDebug("copilot {Args}", args);

        try
        {
            Process? process;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use CreateProcessW directly to set CREATE_NEW_CONSOLE, ensuring the
                // copilot process gets its own console. This is required for graceful
                // Ctrl+C termination to work without affecting the daemon's console.
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;

                var cmdLine = $"copilot {args}";
                var flags = CREATE_NEW_CONSOLE;

                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                    flags, IntPtr.Zero, repoPath, ref si, out var pi))
                {
                    _logger.LogError("CreateProcessW failed for {IssueKey} (error: {Error})",
                        session.IssueKey, Marshal.GetLastWin32Error());
                    return null;
                }

                session.ProcessId = pi.dwProcessId;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);

                try
                {
                    using var proc = Process.GetProcessById(pi.dwProcessId);
                    session.ProcessStartTime = GetProcessStartTime(proc);
                }
                catch
                {
                    session.ProcessStartTime = DateTimeOffset.UtcNow;
                }

                process = null; // Already tracked via PID
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "copilot",
                    Arguments = args,
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                };

                process = Process.Start(psi);
                if (process is null)
                {
                    _logger.LogError("Failed to start copilot process for {IssueKey}", session.IssueKey);
                    return null;
                }

                session.ProcessId = process.Id;
                session.ProcessStartTime = GetProcessStartTime(process);
                process.Dispose();
            }

            session.Status = SessionStatus.Running;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.LastVerifiedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Copilot launched for {IssueKey}: PID={Pid}", session.IssueKey, session.ProcessId);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception launching copilot for {IssueKey}", session.IssueKey);
            return null;
        }
    }

    /// <summary>
    /// Checks if a tracked process is still alive and matches the recorded start time.
    /// </summary>
    public ProcessLivenessResult CheckProcess(DispatchSession session)
    {
        if (session.ProcessId is not { } pid)
            return ProcessLivenessResult.Dead;

        try
        {
            var process = Process.GetProcessById(pid);

            // Verify start time to detect PID reuse
            if (session.ProcessStartTime is { } expectedStart)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                {
                    _logger.LogDebug("PID {Pid} start time mismatch: expected {Expected}, got {Actual}",
                        pid, expectedStart, actualStart);
                    process.Dispose();
                    return ProcessLivenessResult.PidReused;
                }
            }

            var alive = !process.HasExited;
            process.Dispose();
            return alive ? ProcessLivenessResult.Alive : ProcessLivenessResult.Dead;
        }
        catch (ArgumentException)
        {
            // Process not found
            return ProcessLivenessResult.Dead;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking process {Pid}", pid);
            return ProcessLivenessResult.Dead;
        }
    }

    /// <summary>
    /// Gracefully terminates the process associated with a dispatch session by sending
    /// two interrupt signals (SIGINT on Unix, Ctrl+C on Windows) with a delay between them.
    /// Falls back to a hard kill if the process doesn't exit within the timeout.
    /// Verifies PID + start time to avoid terminating an unrelated process after PID reuse.
    /// Returns true if the process was successfully terminated or was already dead.
    /// </summary>
    public bool TerminateProcess(DispatchSession session)
    {
        if (session.ProcessId is not { } pid)
        {
            _logger.LogDebug("No PID tracked for {Key}, nothing to terminate", session.IssueKey);
            return true;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _logger.LogDebug("Process {Pid} for {Key} not found, already exited", pid, session.IssueKey);
            return true;
        }

        try
        {
            // Verify start time to avoid terminating a different process that reused the PID
            if (session.ProcessStartTime is { } expectedStart)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                {
                    _logger.LogWarning("PID {Pid} for {Key} was reused by another process, skipping termination",
                        pid, session.IssueKey);
                    return true;
                }
            }

            if (process.HasExited)
            {
                _logger.LogDebug("Process {Pid} for {Key} already exited", pid, session.IssueKey);
                return true;
            }

            // Try graceful shutdown: send two interrupt signals with a delay
            _logger.LogInformation("Gracefully terminating copilot process {Pid} for {Key}", pid, session.IssueKey);

            if (TrySendInterruptSignal(pid))
            {
                Thread.Sleep(SignalDelay);

                if (!process.HasExited)
                {
                    _logger.LogDebug("Sending second interrupt signal to PID {Pid}", pid);
                    TrySendInterruptSignal(pid);
                }

                if (process.WaitForExit(GracefulTimeout))
                {
                    _logger.LogInformation("Process {Pid} for {Key} exited gracefully", pid, session.IssueKey);
                    return true;
                }
            }

            // Fall back to hard kill
            _logger.LogWarning("Graceful shutdown timed out for PID {Pid}, forcing termination", pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate process {Pid} for {Key}", pid, session.IssueKey);
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Sends an interrupt signal (SIGINT on Unix, Ctrl+C on Windows) to the specified process.
    /// </summary>
    private bool TrySendInterruptSignal(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TrySendCtrlCWindows(pid);
        else
            return TrySendSigintUnix(pid);
    }

    /// <summary>
    /// Sends Ctrl+C to a process on Windows by launching a short-lived helper process
    /// that attaches to the target's console and sends the event. This avoids calling
    /// FreeConsole on the daemon process which disrupts pseudo-console (ConPTY) sessions.
    /// </summary>
    private bool TrySendCtrlCWindows(int pid)
    {
        try
        {
            // The helper process starts with no console (CREATE_NO_WINDOW via CreateNoWindow),
            // attaches to the target's console, and sends Ctrl+C — completely isolated from
            // the daemon's console.
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \""
                    + "$k = Add-Type -MemberDefinition '"
                    + "[DllImport(\\\"kernel32.dll\\\")] public static extern bool FreeConsole();"
                    + "[DllImport(\\\"kernel32.dll\\\")] public static extern bool AttachConsole(uint p);"
                    + "[DllImport(\\\"kernel32.dll\\\")] public static extern bool SetConsoleCtrlHandler(IntPtr h, bool a);"
                    + "[DllImport(\\\"kernel32.dll\\\")] public static extern bool GenerateConsoleCtrlEvent(uint e, uint g);"
                    + "' -Name K -Namespace W -PassThru;"
                    + "[W.K]::FreeConsole();"
                    + "[W.K]::SetConsoleCtrlHandler([IntPtr]::Zero, $true);"
                    + $"[W.K]::AttachConsole({pid});"
                    + "[W.K]::GenerateConsoleCtrlEvent(0, 0)"
                    + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var helper = Process.Start(psi);
            if (helper is null)
            {
                _logger.LogDebug("Failed to start Ctrl+C helper for PID {Pid}", pid);
                return false;
            }

            return helper.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send Ctrl+C to PID {Pid} via helper", pid);
            return false;
        }
    }

    /// <summary>
    /// Sends SIGINT to a process on Unix via libc kill().
    /// </summary>
    private bool TrySendSigintUnix(int pid)
    {
        try
        {
            return sys_kill(pid, SIGINT) == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send SIGINT to PID {Pid}", pid);
            return false;
        }
    }

    private static string BuildPrompt(CopilotdConfig config, GitHubIssue issue, DispatchSession session)
    {
        var prompt = config.Prompt
            .Replace("$(issue.repo)", issue.Repo)
            .Replace("$(issue.id)", issue.Number.ToString())
            .Replace("$(issue.type)", issue.Type ?? "issue")
            .Replace("$(issue.milestone)", issue.Milestone ?? "none");

        var rule = config.Rules.GetValueOrDefault(session.RuleName);
        if (!string.IsNullOrWhiteSpace(rule?.ExtraPrompt))
        {
            prompt += " " + rule.ExtraPrompt;
        }

        return prompt;
    }

    private static string BuildArguments(DispatchSession session, string prompt, DispatchRule? rule)
    {
        var args = new List<string>
        {
            "--remote",
            $"--resume={session.CopilotSessionId}",
            "-i", $"\"{EscapeArg(prompt)}\"",
            "--allow-all-tools",
        };

        if (rule?.Yolo == true)
        {
            args.Add("--yolo");
        }

        return string.Join(' ', args);
    }

    private static string EscapeArg(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static DateTimeOffset? GetProcessStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    #region Platform Interop

    // Windows process creation API (for launching copilot with its own console)
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const uint CTRL_C_EVENT = 0;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    // Unix signal API
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    private const int SIGINT = 2;

    #endregion
}

public enum ProcessLivenessResult
{
    Alive,
    Dead,
    PidReused,
}
