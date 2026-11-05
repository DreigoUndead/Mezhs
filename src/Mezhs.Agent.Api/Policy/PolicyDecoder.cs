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

    // Project policies are standalone policy definitions, never an Agent server configuration.
    public PolicyContext DecodeProjectFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "agent.yaml", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), ".mezhs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project policy must be an absolute path to .mezhs/agent.yaml.");

        var fullPath = Path.GetFullPath(path);
        try
        {
            return DecodeDefinition(fullPath, File.ReadAllText(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read project policy '{fullPath}': {ex.Message}", ex);
        }
    }

    // The stored effective settings use the same YAML shape as PolicyDefinition.
    // Never resolve a live registry entry or reread a project file during recovery.
    public PolicyContext DecodeSnapshot(string policyId, string snapshot) =>
        DecodeDefinition(policyId, snapshot);

    private PolicyContext DecodeDefinition(string id, string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new InvalidOperationException($"Policy snapshot or file '{id}' is empty.");
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidOperationException("Policy must contain one YAML mapping document.");
            return Compile(id, YamlModelMapper.Map<PolicyDefinition>(root, $"policy '{id}'"));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid policy snapshot or file '{id}': {ex.Message}", ex);
        }
    }

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
        var name = string.IsNullOrWhiteSpace(definition.Name) ? id : definition.Name.Trim();
        if (string.IsNullOrWhiteSpace(definition.ConnectionId))
            throw new InvalidOperationException($"Policy '{id}' must define a nonempty connectionId.");
        var connectionId = definition.ConnectionId.Trim();
        var defaultModel = string.IsNullOrWhiteSpace(definition.DefaultModel)
            ? null
            : definition.DefaultModel.Trim();
        var instructions = definition.Instructions?.Trim() ?? string.Empty;

        var settings = new PolicySettings(
            name,
            connectionId,
            defaultModel,
            instructions,
            new PolicyCommandSettings(
                NormalizeCommandNames(commands.Allow, $"policies.{id}.commands.allow"),
                NormalizeCommandNames(commands.Deny, $"policies.{id}.commands.deny")),
            new PolicyEnvironmentSettings(
                NormalizeEnvironmentNames(definition.Environment.Allow, $"policies.{id}.environment.allow")),
            new PolicyCompletionSettings(
                completion.RequireDone,
                NormalizeSuccessfulRequirements(
                    completion.RequiredSuccessfulCommands,
                    $"policies.{id}.completion.requiredSuccessfulCommands")),
            new PolicyLimitsSettings(limits.CommandTimeoutSeconds));

        return new PolicyContext(
            id,
            settings,
            CompileModelInstructions(settings),
            _serializer.Serialize(settings),
            CompileCompletionClaim(settings),
            CompileCompletionValidators(settings),
            CompileActionRules(settings));
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
                var missing = required.Where(requirement => !context.Execution.Evidence.Any(evidence =>
                        MatchesSuccessfulEvidence(context.Execution.Execution, evidence, requirement)))
                    .ToArray();
                return missing.Length == 0
                    ? null
                    : $"Completion requires successful command evidence for {string.Join(", ", missing.Select(command => $"<{command}>"))}.";
            }
        ];
    }

    private static bool MatchesSuccessfulEvidence(
        ExecutionEvidence root,
        ExecutionEvidence evidence,
        string requirement)
    {
        if (evidence.Status != AgentExecutionStatus.Completed ||
            !string.Equals(evidence.CorrelationId, root.CorrelationId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.ParentExecutionId, root.ExecutionId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Registry.TryGet(requirement, out var command))
            return command.Behavior is CommandBehavior.Shell or CommandBehavior.Attach &&
                string.Equals(evidence.CommandName, command.Name, StringComparison.OrdinalIgnoreCase) &&
                (command.Behavior != CommandBehavior.Shell ||
                 (evidence.Kind == AgentExecutionKind.Shell && evidence.ExitCode == 0));

        if (evidence.Kind != AgentExecutionKind.Shell ||
            !string.Equals(evidence.CommandName, "SH", StringComparison.OrdinalIgnoreCase) ||
            evidence.ExitCode != 0)
            return false;
        return requirement.EndsWith('*')
            ? evidence.Request.StartsWith(requirement[..^1], StringComparison.Ordinal)
            : string.Equals(evidence.Request, requirement, StringComparison.Ordinal);
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

        if (settings.Commands.Allow.Contains("ATTACH", StringComparer.OrdinalIgnoreCase) &&
            !settings.Commands.Deny.Contains("ATTACH", StringComparer.OrdinalIgnoreCase))
            rules.Add("To stage locally generated attachments for your own next agent message, use <ATTACH> on its own line, list one file path per line, then </ATTACH>. Uploading does not send a message; the agent runtime includes the files in the next ordinary turn. Do not use /v1/messages from shell.");

        if (settings.Completion.RequireDone)
        {
            var done = Registry.Get(CommandBehavior.Complete);
            rules.Add(
                $"Signal completion by returning <{done.Name}> on a line by itself. " +
                $"If <{done.Name}> follows executable commands in the same reply, MEŽS may finish immediately after those commands succeed and completion policy is satisfied; omit <{done.Name}> when you need to inspect their results first.");
        }

        rules.Add("To terminate the execution unsuccessfully without completing policy requirements, emit <ABORT> on its own line. Do not include other commands in that reply.");

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
            if (!Registry.TryGet(value, out var definition) || definition.Behavior is CommandBehavior.Complete or CommandBehavior.Abort)
                throw new InvalidOperationException($"{path} contains unknown executable command '{raw}'.");
            if (seen.Add(value))
                result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<string> NormalizeSuccessfulRequirements(
        IEnumerable<string>? values,
        string path)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException($"{path} contains an empty successful command requirement.");
            var candidate = raw.Trim();
            var knownCommand = Registry.TryGet(candidate, out var command);
            if (knownCommand && command.Behavior is CommandBehavior.Complete or CommandBehavior.Abort)
                throw new InvalidOperationException($"{path} cannot require terminal command {candidate}.");
            var value = knownCommand ? command!.Name : raw;
            if (value == "*")
                throw new InvalidOperationException($"{path} requires a nonempty prefix before the wildcard.");
            if (value.EndsWith('*') && value[..^1].Contains('*'))
                throw new InvalidOperationException($"{path} supports only a single trailing '*' prefix wildcard.");
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