using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// The reconciliation engine. Compares desired dispatches (from rules + issues)
/// against observed state (persisted sessions + live processes) and takes
/// corrective action to converge.
///
/// Truth sources (in order of precedence for conflicts):
/// 1. Live process status (ground truth for "is it actually running?")
/// 2. Current GitHub issue matches (ground truth for "should it be running?")
/// 3. Persisted state (bookkeeping, used as starting point)
/// </summary>
public sealed class ReconciliationEngine
{
    private readonly ProcessManager _processManager;
    private readonly GhCliService _ghCli;
    private readonly StateStore _stateStore;
    private readonly ILogger<ReconciliationEngine> _logger;

    public ReconciliationEngine(
        ProcessManager processManager,
        GhCliService ghCli,
        StateStore stateStore,
        ILogger<ReconciliationEngine> logger)
    {
        _processManager = processManager;
        _ghCli = ghCli;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full reconciliation cycle. Used both at startup and on each poll.
    /// </summary>
    public void Reconcile(CopilotdConfig config, DaemonState state)
    {
        _logger.LogInformation("Starting reconciliation cycle");

        // Step 1: Verify all tracked non-terminal sessions against live processes
        VerifyTrackedSessions(state);

        // Step 2: Gather all matching issues from configured rules
        var desiredDispatches = ComputeDesiredDispatches(config);

        // Step 3: Reconcile desired vs observed
        ReconcileDesiredVsObserved(config, state, desiredDispatches);

        // Step 4: Dispatch pending sessions
        DispatchPendingSessions(config, state);

        // Step 5: Persist corrected state
        state.LastPollTime = DateTimeOffset.UtcNow;
        _stateStore.SaveState(state);

        _logger.LogInformation("Reconciliation complete: {Active} active, {Pending} pending, {Terminal} terminal sessions",
            state.Sessions.Values.Count(s => s.Status == SessionStatus.Running),
            state.Sessions.Values.Count(s => s.Status == SessionStatus.Pending),
            state.Sessions.Values.Count(s => s.IsTerminal));
    }

    /// <summary>
    /// Step 1: For each non-terminal tracked session, verify the process is still alive.
    /// </summary>
    private void VerifyTrackedSessions(DaemonState state)
    {
        foreach (var (key, session) in state.Sessions)
        {
            if (session.IsTerminal)
                continue;

            if (session.Status is SessionStatus.Pending)
                continue;

            var result = _processManager.CheckProcess(session);

            switch (result)
            {
                case ProcessLivenessResult.Alive:
                    session.LastVerifiedAt = DateTimeOffset.UtcNow;
                    _logger.LogDebug("Session {Key} PID {Pid} is alive", key, session.ProcessId);
                    break;

                case ProcessLivenessResult.Dead:
                    _logger.LogInformation("Session {Key} PID {Pid} is dead, marking orphaned", key, session.ProcessId);
                    session.Status = SessionStatus.Orphaned;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    break;

                case ProcessLivenessResult.PidReused:
                    _logger.LogWarning("Session {Key} PID {Pid} was reused by another process, marking orphaned", key, session.ProcessId);
                    session.Status = SessionStatus.Orphaned;
                    session.ProcessId = null;
                    session.ProcessStartTime = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    break;
            }
        }
    }

    /// <summary>
    /// Step 2: Query GitHub for all issues that currently match configured rules.
    /// Returns a set of issue keys that should have active dispatches.
    /// </summary>
    private Dictionary<string, (GitHubIssue Issue, string RuleName)> ComputeDesiredDispatches(CopilotdConfig config)
    {
        var desired = new Dictionary<string, (GitHubIssue, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ruleName, rule) in config.Rules)
        {
            foreach (var repo in rule.Repos)
            {
                try
                {
                    var issues = _ghCli.QueryIssues(repo, rule);
                    foreach (var issue in issues)
                    {
                        // Double-check rule match (gh filters are best-effort)
                        if (!rule.Matches(issue))
                            continue;

                        if (!desired.ContainsKey(issue.Key))
                        {
                            desired[issue.Key] = (issue, ruleName);
                            _logger.LogDebug("Desired dispatch: {Key} via rule '{Rule}'", issue.Key, ruleName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error querying issues for {Repo} with rule '{Rule}'", repo, ruleName);
                }
            }
        }

        _logger.LogInformation("Found {Count} issues matching configured rules", desired.Count);
        return desired;
    }

    /// <summary>
    /// Step 3: Compare desired dispatches against tracked sessions.
    /// - Issues without active sessions → create pending
    /// - Tracked sessions whose issues no longer match → mark completed
    /// - Orphaned sessions for still-matching issues → re-dispatch if retries remain
    /// </summary>
    private void ReconcileDesiredVsObserved(
        CopilotdConfig config,
        DaemonState state,
        Dictionary<string, (GitHubIssue Issue, string RuleName)> desired)
    {
        // Handle sessions for issues that no longer match
        foreach (var (key, session) in state.Sessions)
        {
            if (session.IsTerminal)
                continue;

            if (!desired.ContainsKey(key))
            {
                _logger.LogInformation("Issue {Key} no longer matches rules, marking session completed", key);
                session.Status = SessionStatus.Completed;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        // Handle desired issues that need new or re-dispatched sessions
        foreach (var (issueKey, (issue, ruleName)) in desired)
        {
            if (state.Sessions.TryGetValue(issueKey, out var existing))
            {
                switch (existing.Status)
                {
                    case SessionStatus.Running:
                    case SessionStatus.Dispatching:
                    case SessionStatus.Pending:
                        // Already active, nothing to do
                        continue;

                    case SessionStatus.Orphaned when existing.CanRetry:
                        _logger.LogInformation("Re-dispatching orphaned session {Key} (retry {N}/{Max})",
                            issueKey, existing.RetryCount + 1, DispatchSession.MaxRetries);
                        existing.Status = SessionStatus.Pending;
                        existing.RetryCount++;
                        existing.CopilotSessionId = Guid.NewGuid().ToString();
                        existing.ProcessId = null;
                        existing.ProcessStartTime = null;
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                        continue;

                    case SessionStatus.Orphaned:
                    case SessionStatus.Failed:
                        if (!existing.CanRetry)
                        {
                            _logger.LogWarning("Session {Key} exceeded max retries, marking failed", issueKey);
                            existing.Status = SessionStatus.Failed;
                            existing.UpdatedAt = DateTimeOffset.UtcNow;
                            continue;
                        }
                        continue;

                    case SessionStatus.Completed:
                        // Issue re-appeared after completion — don't re-dispatch automatically
                        _logger.LogDebug("Issue {Key} matches but session already completed, skipping", issueKey);
                        continue;
                }
            }
            else
            {
                // New issue, create pending session
                _logger.LogInformation("New issue {Key} matched by rule '{Rule}', creating pending dispatch", issueKey, ruleName);
                state.Sessions[issueKey] = new DispatchSession
                {
                    IssueKey = issueKey,
                    Repo = issue.Repo,
                    IssueNumber = issue.Number,
                    RuleName = ruleName,
                    CopilotSessionId = Guid.NewGuid().ToString(),
                    Status = SessionStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }
        }
    }

    /// <summary>
    /// Step 4: Launch copilot for all pending sessions.
    /// </summary>
    private void DispatchPendingSessions(CopilotdConfig config, DaemonState state)
    {
        var pending = state.Sessions.Values
            .Where(s => s.Status == SessionStatus.Pending)
            .ToList();

        foreach (var session in pending)
        {
            session.Status = SessionStatus.Dispatching;
            session.UpdatedAt = DateTimeOffset.UtcNow;

            // We need the issue data for prompt building
            var issue = new GitHubIssue
            {
                Number = session.IssueNumber,
                Repo = session.Repo,
            };

            var result = _processManager.LaunchCopilot(session, config, issue);
            if (result is null)
            {
                _logger.LogWarning("Failed to launch copilot for {Key}", session.IssueKey);
                session.Status = SessionStatus.Failed;
                session.RetryCount++;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
            // else: session was updated in-place by LaunchCopilot
        }
    }
}
