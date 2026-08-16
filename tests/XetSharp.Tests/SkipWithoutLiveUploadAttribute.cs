using XetSharp.Hub;

namespace XetSharp.Tests;

/// <summary>
/// Skips a test unless <c>XETSHARP_LIVE_UPLOAD_REPO</c> names a repository to upload into and a Hub
/// token with write access to it is available. Unlike the live download tests, these ones write to
/// a real repository, so they need credentials and a repository the runner is willing to have
/// scribbled on:
/// <code>XETSHARP_LIVE_UPLOAD_REPO=you/xetsharp-scratch dotnet run --project tests/XetSharp.Tests</code>
/// Set <c>XETSHARP_LIVE_UPLOAD_REPO_TYPE</c> to <c>dataset</c> or <c>space</c> for anything but a model.
/// </summary>
public sealed class SkipWithoutLiveUploadAttribute()
    : SkipAttribute("XETSHARP_LIVE_UPLOAD_REPO is not set, or no Hub token was found; live upload tests are opt-in.")
{
    /// <summary>The repository to upload into, or null when these tests should be skipped.</summary>
    public static XetRepository? Repository
    {
        get
        {
            if (Environment.GetEnvironmentVariable("XETSHARP_LIVE_UPLOAD_REPO") is not { Length: > 0 } id ||
                HuggingFaceCredentials.ResolveToken() is null)
            {
                return null;
            }

            return Environment.GetEnvironmentVariable("XETSHARP_LIVE_UPLOAD_REPO_TYPE")?.ToLowerInvariant() switch
            {
                "dataset" => XetRepository.Dataset(id),
                "space" => XetRepository.Space(id),
                _ => XetRepository.Model(id),
            };
        }
    }

    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(Repository is null);
}
