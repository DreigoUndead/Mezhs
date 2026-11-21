using System.ComponentModel.DataAnnotations;

namespace Mezhs.Agent.Policy;

public sealed class PolicyDefinition
{
    public string? Name { get; set; }

    [Required]
    public string? ConnectionId { get; set; }

    public string? DefaultModel { get; set; }
    public string Instructions { get; set; } = "";

    [Required]
    public PolicyCommandsDefinition? Commands { get; set; }

    public PolicyEnvironmentDefinition Environment { get; set; } = new();

    [Required]
    public PolicyCompletionDefinition? Completion { get; set; }

    public PolicyLimitsDefinition Limits { get; set; } = new();
}

public sealed class PolicyCommandsDefinition
{
    public List<string> Allow { get; set; } = [];
    public List<string> Deny { get; set; } = [];
}

public sealed class PolicyEnvironmentDefinition
{
    public List<string> Allow { get; set; } = [];
}

public sealed class PolicyCompletionDefinition
{
    public bool RequireDone { get; set; } = true;
    public List<string> RequiredSuccessfulCommands { get; set; } = [];
}

public sealed class PolicyLimitsDefinition
{
    [Range(1, int.MaxValue)]
    public int CommandTimeoutSeconds { get; set; } = 60;
    [Range(0, int.MaxValue)]
    public int? Limit { get; set; }
    [Range(0, int.MaxValue)]
    public int? LimitStart { get; set; }
    [Range(0, int.MaxValue)]
    public int? LimitEnd { get; set; }
    public string? Regex { get; set; }
}

public sealed record PolicySettings(
    string Name,
    string ConnectionId,
    string? DefaultModel,
    string Instructions,
    PolicyCommandSettings Commands,
    PolicyEnvironmentSettings Environment,
    PolicyCompletionSettings Completion,
    PolicyLimitsSettings Limits);

public sealed record PolicyCommandSettings(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny);

public sealed record PolicyEnvironmentSettings(IReadOnlyList<string> Allow);

public sealed record PolicyCompletionSettings(
    bool RequireDone,
    IReadOnlyList<string> RequiredSuccessfulCommands);

public sealed record PolicyLimitsSettings(
    int CommandTimeoutSeconds,
    int? Limit = null,
    int? LimitStart = null,
    int? LimitEnd = null,
    string? Regex = null);
