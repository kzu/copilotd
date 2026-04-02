using System.CommandLine;
using Copilotd.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Copilotd.Commands;

public static class ConfigCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("config", "Manage copilotd configuration");

        var setOption = new Option<string?>("--set") { Description = "Set a config value in key=value format (e.g., repo_home=/path/to/repos)" };
        command.Options.Add(setOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var setValue = parseResult.GetValue(setOption);

                if (string.IsNullOrWhiteSpace(setValue))
                {
                    // Display current config
                    var config = stateStore.LoadConfig();
                    ConsoleOutput.Info($"repo_home = {config.RepoHome ?? "(not set)"}");
                    ConsoleOutput.Info($"prompt = {config.Prompt}");
                    ConsoleOutput.Info($"current_user = {config.CurrentUser ?? "(not set)"}");
                    ConsoleOutput.Info($"rules = {config.Rules.Count} rule(s)");
                    return 0;
                }

                var eqIdx = setValue.IndexOf('=');
                if (eqIdx <= 0)
                {
                    ConsoleOutput.Error("Invalid format. Use --set key=value");
                    return 1;
                }

                var key = setValue[..eqIdx].Trim().ToLowerInvariant();
                var value = setValue[(eqIdx + 1)..].Trim();
                var cfg = stateStore.LoadConfig();

                switch (key)
                {
                    case "repo_home":
                        if (value.StartsWith('~'))
                        {
                            value = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                value[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        }
                        cfg.RepoHome = Path.GetFullPath(value);
                        ConsoleOutput.Success($"repo_home set to: {cfg.RepoHome}");
                        break;

                    case "prompt":
                        cfg.Prompt = value;
                        ConsoleOutput.Success("prompt updated.");
                        break;

                    default:
                        ConsoleOutput.Error($"Unknown config key: {key}");
                        ConsoleOutput.Info("Valid keys: repo_home, prompt");
                        return 1;
                }

                stateStore.SaveConfig(cfg);
                return 0;
            }, logger);
        });

        return command;
    }
}
