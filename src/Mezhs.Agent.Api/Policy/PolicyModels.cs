using System.ComponentModel.DataAnnotations;

namespace Mezhs.Agent.Policy;

public sealed class PolicyDefinition
{
    [Required]
    public string? ConnectionId { get; set; }

    public string Instructions { get; set; } = "";

    [Required]
    public PolicyCommandsDefinition? Commands { get; set; }

    public PolicyEnvironmentDefinition Environment { get; set; } = new();

    [Required]
    public PolicyCompletionDefinition? Completion { get; set; }

    [Required]
    public PolicyLimitsDefinition? Limits { get; set; }
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
    public int MaxTurns { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int CommandTimeoutSeconds { get; set; } = 120;
}

public sealed record PolicySettings(
    string ConnectionId,
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
    int MaxTurns,
    int CommandTimeoutSeconds);
