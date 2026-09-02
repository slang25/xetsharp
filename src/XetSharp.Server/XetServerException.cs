namespace XetSharp.Server;

/// <summary>
/// A request the server refuses, carrying the HTTP status the refusal is reported with. The
/// message is written to the client, so it says what was wrong with the request and nothing
/// about the server's insides.
/// </summary>
public sealed class XetServerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static XetServerException BadRequest(string message) => new(400, message);

    public static XetServerException NotFound(string message) => new(404, message);
}
