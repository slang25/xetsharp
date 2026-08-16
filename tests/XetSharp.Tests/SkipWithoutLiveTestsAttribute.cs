namespace XetSharp.Tests;

/// <summary>
/// Skips a test unless <c>XETSHARP_LIVE_TESTS=1</c>. Live tests talk to the real Hugging Face Hub
/// and CDN, so they are off by default: CI stays hermetic and a laptop without a network still runs
/// the whole suite. They need no credentials — every repository they touch is public.
/// </summary>
public sealed class SkipWithoutLiveTestsAttribute()
    : SkipAttribute("XETSHARP_LIVE_TESTS is not set; live Hub interop tests are opt-in.")
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("XETSHARP_LIVE_TESTS") is { Length: > 0 } value &&
        value is not ("0" or "false" or "False");

    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(!Enabled);
}
