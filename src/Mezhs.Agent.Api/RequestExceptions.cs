namespace Mezhs.Agent;

public sealed class RequestValidationException(string message) : Exception(message);
public sealed class ResourceNotFoundException(string message) : Exception(message);
