using System.Text;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Services;

public sealed class AgentPromptBuilder
{
    private const int MaxCommandResultCharacters = 16_000;

    public string BuildInitial(ExecutionRecord execution, PolicyContext policy)
    {
        var policyInstructions = string.IsNullOrWhiteSpace(policy.ModelInstructions)
            ? ""
            : $"""
               Policy instructions:
               {policy.ModelInstructions}

               """;
        return $"""
            [MEŽS AGENT EXECUTION {execution.ExecutionId}]

            {policyInstructions}Task:
            {execution.Request}

            Agent command protocol:
            - Only use command types explicitly allowed by the applicable policy.
            - Command results returned by MEŽS are execution evidence; do not claim an action succeeded without that evidence.
            """;
    }

    public string BuildContinue() =>
        "Continue the assigned agent task according to the applicable policy.";

    public string BuildPolicyCorrection(string? error) =>
        $"""
        Policy rejected your completion claim:
        {error ?? "The completion claim did not satisfy policy."}

        Continue the assigned task and correct the policy violation.
        """;

    public string BuildCommandCorrection(string error) =>
        $"""
        MEŽS rejected an agent command:
        {error}

        Correct the command or continue without it. Do not claim the rejected action occurred.
        """;

    public string BuildCommandResults(IReadOnlyList<AgentCommandResult> results)
    {
        var output = new StringBuilder("Agent command results:\n");
        foreach (var result in results)
        {
            output.Append('[').Append(result.Name).Append("] ")
                .Append(result.Succeeded ? "succeeded" : "failed");
            if (result.ExitCode is { } exitCode)
                output.Append(" (exit ").Append(exitCode).Append(')');
            if (!string.IsNullOrWhiteSpace(result.ExecutionId))
                output.Append(" execution=").Append(result.ExecutionId);
            output.AppendLine();

            if (!string.IsNullOrEmpty(result.Output))
                output.AppendLine(Truncate(result.Output));
            if (!string.IsNullOrWhiteSpace(result.Error))
                output.Append("error: ").AppendLine(result.Error);
        }
        output.Append("Continue the assigned task according to the applicable policy.");
        return output.ToString();
    }

    private static string Truncate(string value) =>
        value.Length <= MaxCommandResultCharacters
            ? value
            : value[..MaxCommandResultCharacters] + "\n[command result truncated in prompt; full result remains in execution history]";
}
