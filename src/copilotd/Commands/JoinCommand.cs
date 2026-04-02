using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Copilotd.Commands;

public static class JoinCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("join", "Take over a copilot session interactively");

        var issueArg = new Argument<string>("issue") { Description = "Issue key to join (e.g., owner/repo#123)" };
        command.Arguments.Add(issueArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();
                var state = stateStore.LoadState();
                var config = stateStore.LoadConfig();

                var issueKey = parseResult.GetValue(issueArg)!;

                if (!state.Sessions.TryGetValue(issueKey, out var session))
                {
                    ConsoleOutput.Error($"No session found for '{issueKey}'.");
                    ConsoleOutput.Info("Use 'copilotd status --all' to see all tracked sessions.");
                    return 1;
                }

                if (string.IsNullOrEmpty(session.CopilotSessionId))
                {
                    ConsoleOutput.Error($"Session for '{issueKey}' has no copilot session ID.");
                    return 1;
                }

                var repoPath = Path.Combine(config.RepoHome ?? ".", session.Repo);
                if (!Directory.Exists(repoPath))
                {
                    ConsoleOutput.Error($"Repo directory not found: {repoPath}");
                    return 1;
                }

                // If the session is currently running, terminate the orchestrated process first
                if (session.Status is SessionStatus.Running or SessionStatus.Dispatching)
                {
                    ConsoleOutput.Info($"Stopping orchestrated session for {issueKey} (PID {session.ProcessId})...");
                    processManager.TerminateProcess(session);
                }
                else if (session.Status is SessionStatus.Joined)
                {
                    ConsoleOutput.Info($"Session was previously joined but not cleaned up. Resuming...");
                }

                // Mark as Joined so the daemon doesn't interfere
                session.Status = SessionStatus.Joined;
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);

                ConsoleOutput.Success($"Joining session {session.CopilotSessionId} for {issueKey}");
                ConsoleOutput.Info("Press Ctrl+C to exit the interactive session.");
                Console.WriteLine();

                // Launch copilot interactively — inherit terminal stdin/stdout/stderr
                var psi = new ProcessStartInfo
                {
                    FileName = "copilot",
                    Arguments = $"--resume={session.CopilotSessionId}",
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };

                Process? interactiveProcess = null;
                try
                {
                    interactiveProcess = Process.Start(psi);
                    if (interactiveProcess is null)
                    {
                        ConsoleOutput.Error("Failed to launch copilot.");
                        RequeueSession(stateStore, issueKey);
                        return 1;
                    }

                    // Track the interactive process PID so the daemon can detect
                    // if it exits (e.g., terminal killed without cleanup)
                    state = stateStore.LoadState();
                    if (state.Sessions.TryGetValue(issueKey, out var tracked))
                    {
                        tracked.ProcessId = interactiveProcess.Id;
                        try { tracked.ProcessStartTime = new DateTimeOffset(interactiveProcess.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                        catch { tracked.ProcessStartTime = DateTimeOffset.UtcNow; }
                        stateStore.SaveState(state);
                    }

                    await interactiveProcess.WaitForExitAsync(ct);
                    var exitCode = interactiveProcess.ExitCode;

                    Console.WriteLine();
                    ConsoleOutput.Info($"Interactive session exited (code {exitCode}).");
                }
                finally
                {
                    if (interactiveProcess is not null)
                    {
                        if (!interactiveProcess.HasExited)
                        {
                            try { interactiveProcess.Kill(entireProcessTree: true); } catch { }
                        }
                        interactiveProcess.Dispose();
                    }

                    // Always re-queue, even on cancellation or crash
                    RequeueSession(stateStore, issueKey);
                }

                return 0;
            }, logger);
        });

        return command;
    }

    private static void RequeueSession(StateStore stateStore, string issueKey)
    {
        try
        {
            var state = stateStore.LoadState();
            if (state.Sessions.TryGetValue(issueKey, out var session) &&
                session.Status == SessionStatus.Joined)
            {
                session.Status = SessionStatus.Pending;
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);
                ConsoleOutput.Info("Session re-queued for orchestrated dispatch.");
            }
        }
        catch
        {
            // Best effort — if state save fails, the daemon's safety net
            // will detect the stale Joined session and reset it
        }
    }
}
