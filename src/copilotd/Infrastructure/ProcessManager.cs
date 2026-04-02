using System.Diagnostics;
using System.Runtime.InteropServices;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Manages launching copilot as independent/detached processes and verifying liveness.
/// Tracks PID + start time to detect PID reuse across daemon restarts.
/// </summary>
public sealed class ProcessManager
{
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

            // Detach on Windows via CREATE_NEW_PROCESS_GROUP
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
            }

            var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("Failed to start copilot process for {IssueKey}", session.IssueKey);
                return null;
            }

            session.ProcessId = process.Id;
            session.ProcessStartTime = GetProcessStartTime(process);
            session.Status = SessionStatus.Running;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.LastVerifiedAt = DateTimeOffset.UtcNow;

            // On Unix, fully detach by not keeping a reference
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                process.Dispose();
            }
            else
            {
                process.Dispose();
            }

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
}

public enum ProcessLivenessResult
{
    Alive,
    Dead,
    PidReused,
}
