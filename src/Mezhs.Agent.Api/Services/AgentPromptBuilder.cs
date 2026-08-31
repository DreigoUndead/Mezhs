using System.Text.Json;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Services;

public sealed record AgentPrompt(string Content, string Origin);

public sealed class AgentPromptBuilder(AgentOptions options)
{
    private const int MaxCommandResultCharacters = 16_000;

    public AgentPrompt BuildInitial(
        ExecutionRecord execution,
        PolicyContext policy,
        bool includePolicyInstructions)
    {
        var bootstrap = includePolicyInstructions ? BuildBootstrap(policy) : string.Empty;
        return new AgentPrompt(
            $"""
            [MEŽS AGENT EXECUTION {execution.ExecutionId}]

            {bootstrap}Task:
            {execution.Request}
            """,
            OriginForExecution(execution));
    }

    public AgentPrompt BuildContinue() =>
        new(options.Messages.Continue!, "agent-runtime");

    public AgentPrompt BuildPolicyCorrection(string? error) =>
        new(Format(options.Messages.PolicyCorrection!, "error", error ?? "The completion claim did not satisfy policy."), "agent-runtime");

    public AgentPrompt BuildCommandCorrection(string error) =>
        new(Format(options.Messages.CommandCorrection!, "error", error), "agent-runtime");

    public AgentPrompt BuildCommandResults(IReadOnlyList<Result> results)
    {
        var payload = results.Select(result => new
        {
            command = result.Name,
            executionId = result.ExecutionId,
            succeeded = result.Succeeded,
            exitCode = result.ExitCode,
            output = string.IsNullOrEmpty(result.Output) ? null : Truncate(result.Output),
            error = string.IsNullOrWhiteSpace(result.Error) ? null : result.Error
        });
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new AgentPrompt(
            Format(options.Messages.CommandResults!, "json", json),
            "command-result");
    }

    private string BuildBootstrap(PolicyContext policy)
    {
        var policyInstructions = string.IsNullOrWhiteSpace(policy.ModelInstructions)
            ? string.Empty
            : $"""
               Policy instructions:
               {policy.ModelInstructions}

               """;
        var shellContext = ShellAllowed(policy)
            ? Format(
                Format(options.Messages.ShellContext!, "shell", HostShellDescription()),
                "timeout",
                policy.Settings.Limits.CommandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\n\n"
            : string.Empty;
        return $"""
            {policyInstructions}{shellContext}{options.Messages.ProtocolIntro}

            """;
    }

    private static bool ShellAllowed(PolicyContext policy)
    {
        var shell = Registry.Get(CommandBehavior.Shell).Name;
        return policy.Settings.Commands.Allow.Contains(shell, StringComparer.OrdinalIgnoreCase) &&
               !policy.Settings.Commands.Deny.Contains(shell, StringComparer.OrdinalIgnoreCase);
    }

    private static string HostShellDescription() =>
        OperatingSystem.IsWindows() ? "cmd.exe on Windows" : "/bin/sh on Unix-like systems";

    private static string OriginForExecution(ExecutionRecord execution) =>
        string.Equals(execution.Source, "manual", StringComparison.OrdinalIgnoreCase)
            ? "human"
            : execution.Source;

    private static string Truncate(string value) =>
        value.Length <= MaxCommandResultCharacters
            ? value
            : value[..MaxCommandResultCharacters] + "\n[command result truncated in prompt; full result remains in execution history]";

    private static string Format(string template, string name, string value) =>
        template.Replace($"{{{name}}}", value, StringComparison.Ordinal);
}
