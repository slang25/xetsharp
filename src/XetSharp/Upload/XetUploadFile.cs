namespace XetSharp.Upload;

/// <summary>
/// One file to upload: where it should live in the repository, and where its bytes come from.
/// The content is read once, start to finish.
/// </summary>
public sealed class XetUploadFile
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly bool _leaveOpen;

    private XetUploadFile(string path, Func<CancellationToken, ValueTask<Stream>> open, bool leaveOpen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _open = open;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The path the file will have in the repository, e.g. <c>weights/model.safetensors</c>.</summary>
    public string Path { get; }

    /// <summary>Reads a local file, which keeps its name in the repository unless one is given.</summary>
    public static XetUploadFile FromFile(string localPath, string? pathInRepository = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return new XetUploadFile(
            pathInRepository ?? System.IO.Path.GetFileName(localPath),
            _ => ValueTask.FromResult<Stream>(
                new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true)),
            leaveOpen: false);
    }

    /// <summary>
    /// Reads bytes already in memory.
    /// </summary>
    public static XetUploadFile FromBytes(string pathInRepository, ReadOnlyMemory<byte> content) =>
        new(pathInRepository, _ => ValueTask.FromResult<Stream>(new MemoryStream(content.ToArray(), writable: false)), leaveOpen: false);

    /// <summary>
    /// Reads an open stream, which the caller keeps ownership of. The stream is read from its
    /// current position to its end.
    /// </summary>
    public static XetUploadFile FromStream(string pathInRepository, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new XetUploadFile(pathInRepository, _ => ValueTask.FromResult(content), leaveOpen: true);
    }

    internal async ValueTask<(Stream Content, bool LeaveOpen)> OpenAsync(CancellationToken cancellationToken) =>
        (await _open(cancellationToken).ConfigureAwait(false), _leaveOpen);
}
