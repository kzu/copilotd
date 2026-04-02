using System.Text.Json;
using System.Text.Json.Serialization;

namespace Copilotd.Models;

/// <summary>
/// AOT-safe JSON serialization metadata for all persisted models.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<SessionStatus>)])]
[JsonSerializable(typeof(CopilotdConfig))]
[JsonSerializable(typeof(DaemonState))]
[JsonSerializable(typeof(DispatchRule))]
[JsonSerializable(typeof(DispatchSession))]
[JsonSerializable(typeof(GitHubIssue))]
[JsonSerializable(typeof(List<GitHubIssue>))]
[JsonSerializable(typeof(List<GhRepo>))]
[JsonSerializable(typeof(GhAuthStatus))]
public partial class CopilotdJsonContext : JsonSerializerContext;

/// <summary>
/// Represents a repo from gh repo list --json.
/// </summary>
public sealed class GhRepo
{
    [JsonPropertyName("nameWithOwner")]
    public string NameWithOwner { get; set; } = "";
}

/// <summary>
/// Represents gh auth status --json output (simplified).
/// </summary>
public sealed class GhAuthStatus
{
    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}
