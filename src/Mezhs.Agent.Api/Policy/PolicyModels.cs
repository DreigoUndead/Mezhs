namespace Mezhs.Agent.Policy;

public sealed class PolicyDefinition
{
    public string ConnectionId { get; set; } = "";
    public string Instructions { get; set; } = "";
    public PolicyCompletionDefinition Completion { get; set; } = new();
    public PolicyLimitsDefinition Limits { get; set; } = new();
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
    PolicyCompletionSettings Completion,
    PolicyLimitsSettings Limits);

public sealed record PolicyCompletionSettings(bool RequireDone);
public sealed record PolicyLimitsSettings(int MaxTurns);
