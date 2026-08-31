using System.ComponentModel.DataAnnotations;

namespace Mezhs.Agent.Policy;

public sealed class PolicyDefinition
{
    [Required]
    public string? ConnectionId { get; set; }

    public string Instructions { get; set; } = "";

    [Required]
    public PolicyCommandsDefinition? Commands { get; set; }

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

public sealed class PolicyCompletionDefinition
{
    public bool RequireDone { get; set; } = true;
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
    PolicyCompletionSettings Completion,
    PolicyLimitsSettings Limits);

public sealed record PolicyCommandSettings(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny);

public sealed record PolicyCompletionSettings(bool RequireDone);
public sealed record PolicyLimitsSettings(
    int MaxTurns,
    int CommandTimeoutSeconds);
