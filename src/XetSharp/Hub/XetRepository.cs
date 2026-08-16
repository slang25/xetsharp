namespace XetSharp.Hub;

/// <summary>The three kinds of repository the Hub hosts, each with its own URL prefix.</summary>
public enum XetRepositoryType
{
    Model,
    Dataset,
    Space,
}

/// <summary>
/// A repository at a specific revision. Xet tokens are scoped to exactly this pair, so a different
/// branch of the same repository needs its own token.
/// </summary>
/// <param name="Type">Model, dataset or space.</param>
/// <param name="Id">The repository id, e.g. <c>openai-community/gpt2</c>.</param>
/// <param name="Revision">A branch, tag or commit hash. Defaults to <c>main</c>.</param>
public sealed record XetRepository(XetRepositoryType Type, string Id, string Revision = "main")
{
    public const string DefaultRevision = "main";

    public static XetRepository Model(string id, string revision = DefaultRevision) =>
        new(XetRepositoryType.Model, id, revision);

    public static XetRepository Dataset(string id, string revision = DefaultRevision) =>
        new(XetRepositoryType.Dataset, id, revision);

    public static XetRepository Space(string id, string revision = DefaultRevision) =>
        new(XetRepositoryType.Space, id, revision);

    /// <summary>The path segment used under <c>/api/</c>: the plural of the repository type.</summary>
    internal string ApiSegment => Type switch
    {
        XetRepositoryType.Model => "models",
        XetRepositoryType.Dataset => "datasets",
        XetRepositoryType.Space => "spaces",
        _ => throw new ArgumentOutOfRangeException(nameof(Type)),
    };

    /// <summary>
    /// The prefix a resolve URL carries before the repository id. Models sit at the root of the Hub;
    /// the other two are namespaced.
    /// </summary>
    internal string ResolvePrefix => Type switch
    {
        XetRepositoryType.Model => "",
        XetRepositoryType.Dataset => "datasets/",
        XetRepositoryType.Space => "spaces/",
        _ => throw new ArgumentOutOfRangeException(nameof(Type)),
    };

    /// <summary>
    /// The revision as a single URL path segment. Branch names may contain slashes (<c>refs/pr/1</c>),
    /// which the Hub expects percent-encoded rather than as extra path segments.
    /// </summary>
    internal string EncodedRevision => Uri.EscapeDataString(Revision);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Revision);
        if (Id.StartsWith('/') || Id.EndsWith('/'))
        {
            throw new ArgumentException($"Repository id '{Id}' must not start or end with a slash.", nameof(Id));
        }
    }
}
