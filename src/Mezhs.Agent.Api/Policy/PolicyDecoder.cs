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
        if (definition.Commands is null)
            throw new InvalidOperationException($"policies.{id}.commands must be a YAML mapping.");
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
        if (definition.Limits.CommandTimeoutSeconds <= 0)
            throw new InvalidOperationException(
                $"policies.{id}.limits.commandTimeoutSeconds must be greater than zero.");

        var settings = new PolicySettings(
            connectionId,
            instructions,
            new PolicyCommandSettings(
                NormalizeCommandNames(definition.Commands.Allow, $"policies.{id}.commands.allow"),
                NormalizeCommandNames(definition.Commands.Deny, $"policies.{id}.commands.deny")),
            new PolicyCompletionSettings(definition.Completion.RequireDone),
            new PolicyLimitsSettings(
                definition.Limits.MaxTurns,
                definition.Limits.CommandTimeoutSeconds));

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
            ? context => context.CompletionClaimed
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
        var allowed = settings.Commands.Allow.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var denied = settings.Commands.Deny.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            context => denied.Contains(context.Action.Kind)
                ? new PolicyActionRuleResult(
                    PolicyActionRuleDecision.Deny,
                    $"Policy explicitly denies {context.Action.Kind} actions.")
                : allowed.Contains(context.Action.Kind)
                    ? new PolicyActionRuleResult(PolicyActionRuleDecision.Allow)
                    : new PolicyActionRuleResult(PolicyActionRuleDecision.None)
        ];
    }

    private static string CompileModelInstructions(PolicySettings settings)
    {
        var rules = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Instructions))
            rules.Add(settings.Instructions);

        var shellAllowed = settings.Commands.Allow.Contains("SH", StringComparer.OrdinalIgnoreCase) &&
            !settings.Commands.Deny.Contains("SH", StringComparer.OrdinalIgnoreCase);
        if (shellAllowed)
        {
            rules.Add(
                "To execute host shell text, return <SH on a line by itself, then the shell text, then SH> on a line by itself. " +
                "The text between those marker lines is passed to the host shell unchanged. Multiple command blocks run in order.");
        }

        if (settings.Completion.RequireDone)
            rules.Add("Signal completion by returning <DONE> on a line by itself.");
        return string.Join("\n", rules);
    }

    private static IReadOnlyList<string> NormalizeCommandNames(
        IEnumerable<string>? values,
        string path)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!IsCommandName(value))
                throw new InvalidOperationException(
                    $"{path} contains invalid command name '{raw}'. Use A-Z, 0-9, '_' or '-' and start with A-Z.");
            if (seen.Add(value))
                result.Add(value);
        }
        return result;
    }

    private static bool IsCommandName(string value)
    {
        if (value.Length == 0 || value[0] is < 'A' or > 'Z')
            return false;
        return value.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
    }

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
