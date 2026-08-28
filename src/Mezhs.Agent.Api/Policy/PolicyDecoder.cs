using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mezhs.Agent.Policy;

public sealed class PolicyDecoder
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    public IReadOnlyDictionary<string, PolicyContext> DecodePolicies(YamlMappingNode policiesNode)
    {
        var definitions = _deserializer.Deserialize<Dictionary<string, PolicyDefinition>>(
            Serialize(policiesNode))
            ?? throw new InvalidOperationException("Agent policies configuration is empty.");
        if (definitions.Count == 0)
            throw new InvalidOperationException("At least one agent policy must be configured.");

        var result = new Dictionary<string, PolicyContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawId, definition) in definitions)
        {
            var id = rawId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Agent policy id cannot be empty.");
            if (definition is null)
                throw new InvalidOperationException($"policies.{id} must be a YAML mapping.");
            if (!result.TryAdd(id, Compile(id, definition)))
                throw new InvalidOperationException($"Duplicate agent policy id '{id}'.");
        }
        return result;
    }

    private PolicyContext Compile(string id, PolicyDefinition definition)
    {
        if (definition.Completion is null)
            throw new InvalidOperationException($"policies.{id}.completion must be a YAML mapping.");
        if (definition.Limits is null)
            throw new InvalidOperationException($"policies.{id}.limits must be a YAML mapping.");

        var connectionId = definition.ConnectionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new InvalidOperationException($"connectionId is required on agent policy '{id}'.");

        var instructions = definition.Instructions?.Trim() ?? string.Empty;
        if (definition.Limits.MaxTurns <= 0)
            throw new InvalidOperationException(
                $"policies.{id}.limits.maxTurns must be greater than zero.");

        var settings = new PolicySettings(
            connectionId,
            instructions,
            new PolicyCompletionSettings(definition.Completion.RequireDone),
            new PolicyLimitsSettings(definition.Limits.MaxTurns));

        var turnValidators = CompileTurnValidators(settings);
        var completionClaim = CompileCompletionClaim(settings);
        var completionValidators = CompileCompletionValidators(settings);
        var actionRules = CompileActionRules(settings);
        var modelInstructions = CompileModelInstructions(settings);
        var snapshot = _serializer.Serialize(settings);

        return new PolicyContext(
            id,
            settings,
            modelInstructions,
            snapshot,
            turnValidators,
            completionClaim,
            completionValidators,
            actionRules);
    }

    private static IReadOnlyList<Func<PolicyTurnContext, string?>> CompileTurnValidators(
        PolicySettings settings)
    {
        var maxTurns = settings.Limits.MaxTurns;
        return
        [
            context => context.TurnIndex < maxTurns
                ? null
                : $"Agent exceeded the configured limit of {maxTurns} turns."
        ];
    }

    private static Func<PolicyCompletionContext, bool> CompileCompletionClaim(
        PolicySettings settings) =>
        settings.Completion.RequireDone
            ? context => ContainsDone(context.AssistantReply)
            : _ => true;

    private static IReadOnlyList<Func<PolicyCompletionContext, string?>> CompileCompletionValidators(
        PolicySettings settings)
    {
        _ = settings;
        return [];
    }

    private static IReadOnlyList<Func<PolicyActionContext, PolicyActionRuleResult>> CompileActionRules(
        PolicySettings settings)
    {
        _ = settings;
        return [];
    }

    private static string CompileModelInstructions(PolicySettings settings)
    {
        var rules = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Instructions))
            rules.Add(settings.Instructions);
        if (settings.Completion.RequireDone)
            rules.Add("Signal completion by returning DONE on a line by itself.");
        return string.Join("\n", rules);
    }

    private static bool ContainsDone(string content) =>
        content.Split('\n')
            .Select(line => line.Trim().TrimEnd('\r'))
            .Any(line => string.Equals(line, "DONE", StringComparison.Ordinal));

    private static string Serialize(YamlNode node)
    {
        var yaml = new YamlStream();
        yaml.Add(new YamlDocument(node));
        var buffer = new StringBuilder();
        using var writer = new StringWriter(buffer, CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return buffer.ToString();
    }
}
