using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Services;

public sealed class AgentPromptBuilder
{
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

            Capability note:
            - Host shell execution is not enabled in this foundation milestone. Do not claim host commands were executed.
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
}
