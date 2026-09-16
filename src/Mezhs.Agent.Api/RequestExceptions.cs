namespace Mezhs.Agent;

public sealed class AgentCapacityExceededException(string message) : Exception(message);
