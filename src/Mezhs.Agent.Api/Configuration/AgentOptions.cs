using System.ComponentModel.DataAnnotations;
using Mezhs.Agent.Policy;
using Mezhs.Configuration;
using YamlDotNet.Serialization;

namespace Mezhs.Agent.Configuration;

public sealed class AgentOptions : MezhsOptions
{
    [Required]
    public string AgentStorage { get; set; } = "data/agent.sqlite";

    [Required]
    public string Workspace { get; set; } = ".";

    public AgentRuntimeOptions Runtime { get; set; } = new();
    public AgentRuntimeMessages Messages { get; set; } = new();
    public Dictionary<string, AgentManualChatOptions> ManualChats { get; set; } = [];

    [YamlMember(Alias = "policies")]
    public Dictionary<string, object?> PolicyDefinitions { get; set; } = [];

    [YamlIgnore]
    public IReadOnlyDictionary<string, PolicyContext> Policies { get; set; } =
        new Dictionary<string, PolicyContext>(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentRuntimeOptions
{
    [Range(1, int.MaxValue)]
    public int QueueCapacity { get; set; } = 32;

    [Range(1, int.MaxValue)]
    public int MaxConcurrentExecutions { get; set; } = 4;
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

public sealed class AgentManualChatOptions
{
    [Required]
    public string? PolicyId { get; set; }

    public string? ConnectionId { get; set; }
    public string? Model { get; set; }
}
