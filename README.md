# copilotd

An orchestration daemon that watches configured GitHub repositories for issues matching dispatch rules and spawns [Copilot CLI](https://docs.github.com/copilot/how-tos/copilot-cli) sessions to work on them automatically.

## Features

- **Issue watching** — polls GitHub repos for issues matching configurable rules (assigned user, labels, milestone, issue type)
- **Automatic dispatch** — launches `copilot --remote` sessions with templated prompts derived from issue metadata
- **Named dispatch rules** — flexible, composable rules with per-rule launch options (`--yolo`, extra prompts, repo assignments)
- **Self-healing state** — reconciles persisted state, live process status, and GitHub issue matches on every poll cycle and at startup
- **Crash-resilient** — dispatched `copilot` sessions run as independent processes that survive daemon restarts; state is persisted atomically
- **Cross-platform** — works on Windows, macOS, and Linux; publishes as native AOT

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated via `gh auth login`
- [Copilot CLI (`copilot`)](https://docs.github.com/copilot/how-tos/copilot-cli) — authenticated via `copilot login`

## Getting started

```bash
# Clone and build
git clone https://github.com/DamianEdwards/copilotd.git
cd copilotd
dotnet build

# First-run setup (checks dependencies, prompts for config)
./copilotd.sh init          # macOS/Linux
copilotd.cmd init           # Windows

# Start the daemon
./copilotd.sh run           # macOS/Linux
copilotd.cmd run            # Windows
```

Convenience scripts `copilotd.sh` and `copilotd.cmd` in the repo root run the project from source via `dotnet run`, passing all arguments through.

## Commands

| Command | Description |
|---------|-------------|
| `copilotd init` | Interactive first-run setup (dependency checks, auth, repo selection, default rule) |
| `copilotd config` | Display current configuration |
| `copilotd config --set key=value` | Set a config value (`repo_home`, `prompt`) |
| `copilotd rules list` | List all dispatch rules |
| `copilotd rules add <name>` | Add a new dispatch rule |
| `copilotd rules update <name>` | Update an existing rule |
| `copilotd rules delete <name>` | Delete a rule (the `Default` rule cannot be deleted) |
| `copilotd run` | Start the polling daemon |

### Run options

```
--interval <seconds>   Polling interval (default: 60)
--log-level <level>    Enable console logging (debug, info, warning, error)
```

### Rules options

Rules support conditions (`--user`, `--label`, `--milestone`, `--type`) and launch options (`--yolo`, `--prompt`, `--repo`). All conditions are logical AND.

```bash
# Add a rule
copilotd rules add "Dashboard issues" --label area-dashboard --yolo --repo "org/repo"

# Update labels on a rule
copilotd rules update Default --delete-label copilotd --add-label dispatch

# Add/remove repos from a rule
copilotd rules update Default --add-repo "org/repo" --delete-repo "org/old-repo"
```

### Prompt templating

The base prompt supports token replacement:

| Token | Value |
|-------|-------|
| `$(issue.repo)` | Repository in `org/repo` format |
| `$(issue.id)` | Issue number |
| `$(issue.type)` | Issue type (e.g., `bug`) |
| `$(issue.milestone)` | Milestone title |

## Configuration

Stored in `~/.copilotd/`:

- `config.json` — user-managed settings (repo home, prompt template, rules)
- `state.json` — runtime session tracking (auto-managed, self-healing)

Log files are written to `$TEMP/copilotd/logs/` with daily rollover.

## Architecture

- **System.CommandLine** for CLI parsing
- **Spectre.Console** for interactive prompts and formatted output
- **Native AOT** compatible (source-generated JSON serialization)
- Dispatched `copilot` processes are fully independent (not child processes)
- State reconciliation uses three truth sources: persisted state → live process status → current GitHub issue matches

## License

MIT
