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

                int exitCode;
                using (var process = Process.Start(psi))
                {
                    if (process is null)
                    {
                        ConsoleOutput.Error("Failed to launch copilot.");
                        // Revert to Pending so daemon can re-dispatch
                        session.Status = SessionStatus.Pending;
                        session.UpdatedAt = DateTimeOffset.UtcNow;
                        stateStore.SaveState(state);
                        return 1;
                    }

                    await process.WaitForExitAsync(CancellationToken.None);
                    exitCode = process.ExitCode;
                }

                Console.WriteLine();
                ConsoleOutput.Info($"Interactive session exited (code {exitCode}).");

                // Re-queue as Pending so the daemon re-dispatches it
                state = stateStore.LoadState();
                if (state.Sessions.TryGetValue(issueKey, out var updated))
                {
                    updated.Status = SessionStatus.Pending;
                    updated.ProcessId = null;
                    updated.ProcessStartTime = null;
                    updated.UpdatedAt = DateTimeOffset.UtcNow;
                    stateStore.SaveState(state);
                    ConsoleOutput.Info("Session re-queued for orchestrated dispatch.");
                }

                return 0;
            }, logger);
        });

        return command;
    }
}
