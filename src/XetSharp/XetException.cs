using System.Net;

namespace XetSharp;

/// <summary>The base type for every error XetSharp raises deliberately.</summary>
public class XetException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A Hub or CAS request that came back with an error status. <see cref="IsRetryable"/> follows the
/// protocol's classification rather than the status class: a 429 or 5xx is worth retrying, while a
/// 400, 403, 404 or 416 means the request itself was wrong and will stay wrong.
/// </summary>
public sealed class XetApiException(string message, HttpStatusCode? statusCode, Uri? requestUri = null)
    : XetException(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    public Uri? RequestUri { get; } = requestUri;

    public bool IsRetryable => StatusCode is { } status && IsRetryableStatus(status);

    /// <summary>
    /// Whether a response with this status is worth sending again. Covers the protocol's retryable
    /// set (429, 500, 503, 504) plus the two other statuses that always mean "try again": 408
    /// request timeout and 502 bad gateway.
    /// </summary>
    public static bool IsRetryableStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;
}

/// <summary>Thrown when downloaded data does not reconstruct to what the protocol promised.</summary>
public sealed class XetVerificationException(string message) : XetException(message);
