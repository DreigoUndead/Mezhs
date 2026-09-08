using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mezhs.Agent.Policy;

public sealed class PolicyDecoder
{
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    public IReadOnlyDictionary<string, PolicyContext> DecodePolicies(YamlMappingNode policiesNode)
    {
        var definitions = YamlModelMapper.Map<Dictionary<string, PolicyDefinition>>(
            policiesNode,
            "policies");
        if (definitions.Count == 0)
            throw new InvalidOperationException("At least one agent policy must be configured.");

        var result = new Dictionary<string, PolicyContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawId, definition) in definitions)
        {
            var id = rawId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Agent policy id cannot be empty.");
            if (!result.TryAdd(id, Compile(id, definition)))
                throw new InvalidOperationException($"Duplicate agent policy id '{id}'.");
        }
        return result;
    }

    private PolicyContext Compile(string id, PolicyDefinition definition)
    {
        var commands = definition.Commands!;
        var completion = definition.Completion!;
        var limits = definition.Limits!;
        var connectionId = definition.ConnectionId!.Trim();
        var instructions = definition.Instructions?.Trim() ?? string.Empty;

        var settings = new PolicySettings(
            connectionId,
            instructions,
            new PolicyCommandSettings(
                NormalizeCommandNames(commands.Allow, $"policies.{id}.commands.allow"),
                NormalizeCommandNames(commands.Deny, $"policies.{id}.commands.deny")),
            new PolicyEnvironmentSettings(
                NormalizeEnvironmentNames(definition.Environment.Allow, $"policies.{id}.environment.allow")),
            new PolicyCompletionSettings(
                completion.RequireDone,
                NormalizeCommandNames(
                    completion.RequiredSuccessfulCommands,
                    $"policies.{id}.completion.requiredSuccessfulCommands")),
            new PolicyLimitsSettings(limits.MaxTurns, limits.CommandTimeoutSeconds));

        return new PolicyContext(
            id,
            settings,
            CompileModelInstructions(settings),
            _serializer.Serialize(settings),
            CompileTurnValidators(settings),
            CompileCompletionClaim(settings),
            CompileCompletionValidators(settings),
            CompileActionRules(settings));
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
        var required = settings.Completion.RequiredSuccessfulCommands;
        if (required.Count == 0)
            return [];

        return
        [
            context =>
            {
                var missing = required.Where(command => !context.Execution.Evidence.Any(evidence =>
                        string.Equals(evidence.CommandName, command, StringComparison.OrdinalIgnoreCase) &&
                        evidence.Status == AgentExecutionStatus.Completed))
                    .ToArray();
                return missing.Length == 0
                    ? null
                    : $"Completion requires successful command evidence for {string.Join(", ", missing.Select(command => $"<{command}>"))}.";
            }
        ];
    }

    private static IReadOnlyList<Func<PolicyActionContext, PolicyActionRuleResult>> CompileActionRules(
        PolicySettings settings)
    {
        var allowed = settings.Commands.Allow.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var denied = settings.Commands.Deny.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            context => denied.Contains(context.Action.Command.Name)
                ? new PolicyActionRuleResult(
                    PolicyActionRuleDecision.Deny,
                    $"Policy explicitly denies {context.Action.Command.Name} actions.")
                : allowed.Contains(context.Action.Command.Name)
                    ? new PolicyActionRuleResult(PolicyActionRuleDecision.Allow)
                    : new PolicyActionRuleResult(PolicyActionRuleDecision.None)
        ];
    }

    private static string CompileModelInstructions(PolicySettings settings)
    {
        var rules = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Instructions))
            rules.Add(settings.Instructions);

        var shell = Registry.Get(CommandBehavior.Shell);
        if (settings.Commands.Allow.Contains(shell.Name, StringComparer.OrdinalIgnoreCase) &&
            !settings.Commands.Deny.Contains(shell.Name, StringComparer.OrdinalIgnoreCase))
        {
            rules.Add(
                $"To execute host shell text, return <{shell.Name}> on a line by itself, then the shell text, then </{shell.Name}> on a line by itself. " +
                "The text between those tags is passed to the host shell unchanged. Multiple command blocks run in order.");
        }

        if (settings.Completion.RequireDone)
        {
            var done = Registry.Get(CommandBehavior.Complete);
            rules.Add($"Signal completion by returning <{done.Name}> on a line by itself.");
        }

        if (settings.Completion.RequiredSuccessfulCommands.Count > 0)
        {
            rules.Add(
                $"Completion requires successful execution evidence for: {string.Join(", ", settings.Completion.RequiredSuccessfulCommands.Select(command => $"<{command}>"))}.");
        }
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
            if (!Registry.TryGet(value, out var definition) || definition.Behavior == CommandBehavior.Complete)
                throw new InvalidOperationException($"{path} contains unknown executable command '{raw}'.");
            if (seen.Add(value))
                result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<string> NormalizeEnvironmentNames(
        IEnumerable<string>? values,
        string path)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0 || value.Contains('=') || value.Contains('\0'))
                throw new InvalidOperationException($"{path} contains invalid environment variable name '{raw}'.");
            if (value.StartsWith("MEZHS_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{path} cannot allow reserved MEZHS_ environment variable '{value}'.");
            if (seen.Add(value))
                result.Add(value);
        }
        return result;
    }
}
