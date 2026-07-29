using System.Net;

namespace JKBridge.Core.Agent;

public sealed class AgentProtocolException : Exception
{
    public AgentProtocolException(
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string? ErrorCode { get; }

    public HttpStatusCode? StatusCode { get; }
}
