using System.Net;

namespace Dmon.Tools.Calendar;

/// <summary>
/// Raised when the calendar HTTP API returns a non-success status or an unreadable body.
/// </summary>
public sealed class CalendarApiException : Exception
{
    public CalendarApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status returned by the calendar server, when the failure came from a response.</summary>
    public HttpStatusCode? StatusCode { get; }
}
