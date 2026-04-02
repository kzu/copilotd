using System.CommandLine;
using System.CommandLine.Parsing;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class StatusCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("status", "Show daemon status and tracked copilot sessions");

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter sessions by status (pending, dispatching, running, completed, failed, orphaned)"
        };
        command.Options.Add(filterOption);

        var allOption = new Option<bool>("--all")
        {
            Description = "Include ended (completed/failed) sessions"
        };
        command.Options.Add(allOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();

                // Daemon status
                var daemonRunning = stateStore.IsLockHeld();
                var state = stateStore.LoadState();
                var config = stateStore.LoadConfig();

                AnsiConsole.MarkupLine(daemonRunning
                    ? "[green]● Daemon is running[/]"
                    : "[grey]○ Daemon is not running[/]");

                if (state.LastPollTime.HasValue)
                {
                    ConsoleOutput.Info($"  Last poll: {FormatTime(state.LastPollTime.Value)}");
                }

                var watchedRepos = config.Rules.Values
                    .SelectMany(r => r.Repos)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (watchedRepos.Count > 0)
                {
                    ConsoleOutput.Info($"  Watching:  {string.Join(", ", watchedRepos)}");
                }

                ConsoleOutput.Info($"  Rules:     {config.Rules.Count}");
                AnsiConsole.WriteLine();

                // Sessions
                var filterValue = parseResult.GetValue(filterOption);
                var showAll = parseResult.GetValue(allOption);

                SessionStatus? statusFilter = null;
                if (!string.IsNullOrWhiteSpace(filterValue))
                {
                    if (!Enum.TryParse<SessionStatus>(filterValue, ignoreCase: true, out var parsed))
                    {
                        var validValues = string.Join(", ", Enum.GetNames<SessionStatus>().Select(n => n.ToLowerInvariant()));
                        ConsoleOutput.Error($"Unknown status: {filterValue}");
                        ConsoleOutput.Info($"Valid values: {validValues}");
                        return 1;
                    }
                    statusFilter = parsed;
                }

                var sessions = state.Sessions.Values.AsEnumerable();

                if (statusFilter.HasValue)
                {
                    sessions = sessions.Where(s => s.Status == statusFilter.Value);
                }
                else if (!showAll)
                {
                    sessions = sessions.Where(s => !s.IsTerminal);
                }

                var list = sessions.OrderBy(s => s.CreatedAt).ToList();

                if (list.Count == 0)
                {
                    var qualifier = statusFilter.HasValue
                        ? $" with status '{statusFilter.Value.ToString().ToLowerInvariant()}'"
                        : showAll ? "" : " (use --all to include ended sessions)";
                    ConsoleOutput.Info($"No sessions found{qualifier}.");
                    return 0;
                }

                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.AddColumn("Issue");
                table.AddColumn("Rule");
                table.AddColumn("Status");
                table.AddColumn("PID");
                table.AddColumn("Session ID");
                table.AddColumn("Created");
                table.AddColumn("Updated");

                foreach (var s in list)
                {
                    var statusMarkup = s.Status switch
                    {
                        SessionStatus.Running => $"[green]{s.Status}[/]",
                        SessionStatus.Pending or SessionStatus.Dispatching => $"[yellow]{s.Status}[/]",
                        SessionStatus.Failed or SessionStatus.Orphaned => $"[red]{s.Status}[/]",
                        SessionStatus.Completed => $"[grey]{s.Status}[/]",
                        _ => s.Status.ToString()
                    };

                    var pid = s.ProcessId?.ToString() ?? "-";
                    var sessionId = string.IsNullOrEmpty(s.CopilotSessionId)
                        ? "-"
                        : s.CopilotSessionId[..Math.Min(8, s.CopilotSessionId.Length)];

                    table.AddRow(
                        Markup.Escape(s.IssueKey),
                        Markup.Escape(s.RuleName),
                        statusMarkup,
                        Markup.Escape(pid),
                        Markup.Escape(sessionId),
                        FormatTime(s.CreatedAt),
                        FormatTime(s.UpdatedAt)
                    );
                }

                AnsiConsole.Write(table);
                ConsoleOutput.Info($"{list.Count} session(s)");

                return 0;
            }, logger);
        });

        return command;
    }

    private static string FormatTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h {age.Minutes}m ago";
        return local.ToString("yyyy-MM-dd HH:mm");
    }
}
