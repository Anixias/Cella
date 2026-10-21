namespace Cella.LanguageServer;

public sealed class RequestFailedException(string message) : Exception(message);