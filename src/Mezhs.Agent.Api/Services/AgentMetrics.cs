namespace Mezhs.Agent.Services;

public sealed class AgentMetrics
{
    private long _policyDenials;

    public long PolicyDenials => Interlocked.Read(ref _policyDenials);

    public void RecordPolicyDenial() => Interlocked.Increment(ref _policyDenials);
}
