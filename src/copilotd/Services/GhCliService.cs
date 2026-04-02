using System.Diagnostics;
using System.Text.Json;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// Adapter for the GitHub CLI (gh). Handles dependency checks, auth, repo listing, and issue queries.
/// </summary>
public sealed class GhCliService
{
    private readonly ILogger<GhCliService> _logger;
    private bool _supportsTypeField = true;

    public GhCliService(ILogger<GhCliService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the gh CLI is available on PATH.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            var (exitCode, _) = RunGh("--version");
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether gh is authenticated and returns the current username.
    /// </summary>
    public (bool IsLoggedIn, string? Username) CheckAuth()
    {
        try
        {
            var (exitCode, output) = RunGh("auth status");
            if (exitCode != 0)
                return (false, null);

            // Extract username from "Logged in to github.com account <username>"
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var idx = line.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var rest = line[(idx + 8)..].Trim();
                    var spaceIdx = rest.IndexOf(' ');
                    var username = spaceIdx > 0 ? rest[..spaceIdx] : rest;
                    // Strip trailing parenthetical/punctuation
                    username = username.TrimEnd(')', '(', ' ');
                    if (!string.IsNullOrEmpty(username))
                        return (true, username);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking gh auth");
            return (false, null);
        }
    }

    /// <summary>
    /// Lists repositories the user has access to.
    /// </summary>
    public List<string> ListRepos(int limit = 200)
    {
        var (exitCode, output) = RunGh($"repo list --limit {limit} --json nameWithOwner --jq \".[].nameWithOwner\"");
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to list repos: {Output}", output);
            return [];
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Queries open issues for a repo matching the given rule conditions.
    /// </summary>
    public List<GitHubIssue> QueryIssues(string repo, DispatchRule rule)
    {
        var jsonFields = _supportsTypeField
            ? "number,title,assignees,labels,milestone,type"
            : "number,title,assignees,labels,milestone";

        var args = $"issue list --repo {repo} --state open --json {jsonFields} --limit 100";

        if (rule.User is not null)
            args += $" --assignee {rule.User}";

        foreach (var label in rule.Labels)
            args += $" --label \"{label}\"";

        if (rule.Milestone is not null)
            args += $" --milestone \"{rule.Milestone}\"";

        var (exitCode, output) = RunGh(args);

        // Fall back to querying without 'type' if gh doesn't support it
        if (exitCode != 0 && _supportsTypeField && output.Contains("Unknown JSON field", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("gh CLI does not support 'type' JSON field, retrying without it");
            _supportsTypeField = false;
            args = $"issue list --repo {repo} --state open --json number,title,assignees,labels,milestone --limit 100";

            if (rule.User is not null)
                args += $" --assignee {rule.User}";

            foreach (var label in rule.Labels)
                args += $" --label \"{label}\"";

            if (rule.Milestone is not null)
                args += $" --milestone \"{rule.Milestone}\"";

            (exitCode, output) = RunGh(args);
        }

        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query issues for {Repo}: {Output}", repo, output);
            return [];
        }

        try
        {
            return ParseIssuesJson(output, repo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse issue JSON for {Repo}", repo);
            return [];
        }
    }

    private static List<GitHubIssue> ParseIssuesJson(string json, string repo)
    {
        using var doc = JsonDocument.Parse(json);
        var issues = new List<GitHubIssue>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var issue = new GitHubIssue
            {
                Number = element.GetProperty("number").GetInt32(),
                Title = element.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Repo = repo,
                Labels = [],
            };

            if (element.TryGetProperty("assignees", out var assignees) && assignees.GetArrayLength() > 0)
            {
                var first = assignees[0];
                issue.Assignee = first.TryGetProperty("login", out var login) ? login.GetString() : null;
            }

            if (element.TryGetProperty("labels", out var labels))
            {
                foreach (var label in labels.EnumerateArray())
                {
                    var name = label.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null)
                        issue.Labels.Add(name);
                }
            }

            if (element.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object)
            {
                issue.Milestone = ms.TryGetProperty("title", out var mt) ? mt.GetString() : null;
            }

            if (element.TryGetProperty("type", out var typeEl))
            {
                if (typeEl.ValueKind == JsonValueKind.Object)
                    issue.Type = typeEl.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                else if (typeEl.ValueKind == JsonValueKind.String)
                    issue.Type = typeEl.GetString();
            }

            issues.Add(issue);
        }

        return issues;
    }

    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(30);

    private (int ExitCode, string Output) RunGh(string arguments)
    {
        _logger.LogDebug("Running: gh {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;

        // Read stderr asynchronously to avoid deadlock when pipe buffers fill
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(GhTimeout))
        {
            _logger.LogWarning("gh command timed out after {Timeout}s: gh {Args}", GhTimeout.TotalSeconds, arguments);
            process.Kill();
            return (-1, "gh command timed out");
        }

        var stderr = stderrTask.Result;
        var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
        return (process.ExitCode, output);
    }
}
