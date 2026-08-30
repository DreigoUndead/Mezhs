namespace Mezhs.Agent.Policy;

public sealed class PolicyDefinition
{
    public string ConnectionId { get; set; } = "";
    public string Instructions { get; set; } = "";
    public PolicyCommandsDefinition Commands { get; set; } = new();
    public PolicyCompletionDefinition Completion { get; set; } = new();
    public PolicyLimitsDefinition Limits { get; set; } = new();
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
    public int MaxTurns { get; set; } = 30;
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
public sealed record PolicyLimitsSettings(int MaxTurns);
