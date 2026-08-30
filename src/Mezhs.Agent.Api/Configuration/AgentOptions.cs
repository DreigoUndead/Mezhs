using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Configuration;

public sealed class AgentOptions
{
    public required Uri Listen { get; init; }
    public required Uri MezhsApi { get; init; }
    public required string Storage { get; init; }
    public required IReadOnlyDictionary<string, PolicyContext> Policies { get; init; }
}
