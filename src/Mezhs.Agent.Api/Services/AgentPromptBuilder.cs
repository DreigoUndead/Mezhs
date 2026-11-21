using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;
using Mezhs.Executor;

namespace Mezhs.Agent.Services;

public sealed record AgentPrompt(string Content, string Origin);

public sealed class AgentPromptBuilder(AgentOptions options, ExecutorService executor)
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

    public AgentPrompt BuildContinue(PolicyContext policy) =>
        new(WithCompletionGuidance(options.Messages.Continue!, policy), "agent-runtime");

    public AgentPrompt BuildPolicyCorrection(string? error) =>
        new(Format(options.Messages.PolicyCorrection!, "error", error ?? "The completion claim did not satisfy policy."), "agent-runtime");

    public AgentPrompt BuildCommandCorrection(string error) =>
        new(Format(options.Messages.CommandCorrection!, "error", error), "agent-runtime");

    public AgentPrompt BuildCommandResults(
        IReadOnlyList<Result> results,
        PolicyContext policy)
    {
        var payload = results.Select(result =>
        {
            var displayed = DisplayOutput(result);
            return new
            {
                command = result.Name,
                executionId = result.ExecutionId,
                succeeded = result.Succeeded,
                exitCode = result.ExitCode,
                output = displayed.Output,
                error = string.IsNullOrWhiteSpace(result.Error) ? null : result.Error,
                recoveryCommand = displayed.RecoveryCommand,
                recoveryNote = displayed.RecoveryNote
            };
        });
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new AgentPrompt(
            WithCompletionGuidance(Format(options.Messages.CommandResults!, "json", json), policy),
            "command-result");
    }

    private sealed record DisplayedOutput(
        string? Output, string? RecoveryCommand = null, string? RecoveryNote = null);

    private DisplayedOutput DisplayOutput(Result result)
    {
        if (string.IsNullOrEmpty(result.Output))
            return new DisplayedOutput(null);
        var text = result.Output;
        var abbreviated = false;
        var settings = result.OutputOptions;
        if (settings is not null && (settings.Limit > 0 || settings.Regex is not null))
        {
            var lines = new List<string>();
            using (var reader = new StringReader(text))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    lines.Add(line);
            }
            var selected = lines;
            if (settings.Regex is { } pattern)
            {
                try
                {
                    var matcher = new Regex(pattern, RegexOptions.CultureInvariant,
                        ShellOutputOptions.MatchTimeout);
                    selected = lines.Where(line => matcher.IsMatch(line)).ToList();
                }
                catch (RegexMatchTimeoutException)
                {
                    return RecoveryOutput(result,
                        "[SH regex evaluation timed out; full output remains in the durable Executor record.]");
                }
                abbreviated = selected.Count != lines.Count;
            }

            if (settings.Limit > 0 && selected.Count > settings.Limit)
            {
                var first = selected.Take(settings.LimitStart);
                var last = settings.LimitEnd == 0 ? Enumerable.Empty<string>()
                    : selected.Skip(selected.Count - settings.LimitEnd);
                var shown = first.Concat(last).ToArray();
                var omitted = selected.Count - shown.Length;
                text = string.Join("\n", shown) +
                    $"\n[SH line preview: {shown.Length}/{selected.Count} selected lines shown; {omitted} omitted.]";
                abbreviated = true;
            }
            else text = string.Join("\n", selected);

            if (settings.Regex is not null)
                text += $"\n[SH regex filter: {selected.Count}/{lines.Count} original lines matched.]";
        }

        if (text.Length > MaxCommandResultCharacters)
        {
            text = text[..MaxCommandResultCharacters] +
                "\n[Separate model-prompt character safeguard activated (16000 characters).]";
            abbreviated = true;
        }
        return abbreviated ? RecoveryOutput(result, text) : new DisplayedOutput(text);
    }

    private DisplayedOutput RecoveryOutput(Result result, string preview)
    {
        var command = RecoveryCommand(result.ExecutionId);
        return new DisplayedOutput(preview, command,
            command is null
                ? "Full output is retained in Executor history; a CLI recovery command could not be resolved."
                : "Run recoveryCommand as an SH block to fetch the stored Executor record without rerunning " +
                  "the original shell command. The record's Result is JSON-escaped. " +
                  "limit=0 disables line abbreviation; the separate 16000-character model-prompt safeguard still applies.");
    }

    private string? RecoveryCommand(string? executionId)
    {
        if (!int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            id < 1)
            return null;
        var assembly = typeof(ExecutorApplication).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assembly) || !File.Exists(assembly))
            return null;
        if (OperatingSystem.IsWindows())
            return $"<SH limit=0 regex=\".*\">set \"{ExecutorEnvironment.StorageVariable}={executor.StoragePath}\" && dotnet \"{assembly}\" Get {id}</SH>";
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $"<SH limit=0 regex=\".*\">{ExecutorEnvironment.StorageVariable}={Quote(executor.StoragePath)} dotnet {Quote(assembly)} Get {id}</SH>";
    }

    private string BuildBootstrap(PolicyContext policy)
    {
        var policyInstructions = string.IsNullOrWhiteSpace(policy.ModelInstructions)
            ? string.Empty
            : $"""
               Policy instructions:
               {policy.ModelInstructions.Replace("{OS}", HostOsName(), StringComparison.Ordinal)}

               """;
        var shellContext = ShellAllowed(policy)
            ? Format(
                Format(options.Messages.ShellContext!, "shell", HostShellDescription()),
                "timeout",
                policy.Settings.Limits.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)) + "\n\n"
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

    private static string HostOsName() =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" : "Unknown OS";

    private static string HostShellDescription() =>
        OperatingSystem.IsWindows() ? "cmd.exe on Windows" : "/bin/sh on Unix-like systems";

    private static string OriginForExecution(ExecutionRecord execution) =>
        string.Equals(execution.Source, "manual", StringComparison.OrdinalIgnoreCase)
            ? "human"
            : execution.Source;

    private static string WithCompletionGuidance(string message, PolicyContext policy)
    {
        if (!policy.Settings.Completion.RequireDone)
            return message.TrimEnd();
        var done = Registry.Get(CommandBehavior.Complete);
        return $"{message.TrimEnd()}\nWhen the task is complete, return <{done.Name}> on a line by itself.";
    }

    private static string Format(string template, string name, string value) =>
        template.Replace($"{{{name}}}", value, StringComparison.Ordinal);
}
