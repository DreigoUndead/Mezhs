using System.ComponentModel.DataAnnotations;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Configuration;

public sealed class AgentOptions
{
    public required Uri Listen { get; init; }
    public required Uri MezhsApi { get; init; }
    public required string Storage { get; init; }
    public required AgentRuntimeMessages Messages { get; init; }
    public required IReadOnlyDictionary<string, PolicyContext> Policies { get; init; }
}

public sealed class AgentRuntimeMessages
{
    [Required]
    public string? Continue { get; set; }

    [Required]
    public string? PolicyCorrection { get; set; }

    [Required]
    public string? CommandCorrection { get; set; }

    [Required]
    public string? CommandResults { get; set; }

    [Required]
    public string? ProtocolIntro { get; set; }

    [Required]
    public string? ShellContext { get; set; }
}
