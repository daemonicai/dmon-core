using System.Net;

namespace Dmon.Tools.Dmail;

/// <summary>
/// Raised when the Dmail HTTP API returns a non-success status or an unreadable body.
/// </summary>
public sealed class DmailApiException : Exception
{
    public DmailApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status returned by Dmail, when the failure came from a response.</summary>
    public HttpStatusCode? StatusCode { get; }
}
