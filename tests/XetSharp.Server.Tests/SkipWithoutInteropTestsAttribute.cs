namespace XetSharp.Server.Tests;

/// <summary>
/// Skips a test unless <c>XETSHARP_INTEROP_TESTS=1</c>. Interop tests drive the official Python
/// client — <c>huggingface_hub</c> with the Rust <c>hf_xet</c> engine — against this server, which
/// needs <c>uv</c> on the path and a first-run package download, so they are opt-in.
/// </summary>
public sealed class SkipWithoutInteropTestsAttribute()
    : SkipAttribute("XETSHARP_INTEROP_TESTS is not set; interop tests against the official Python client are opt-in.")
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("XETSHARP_INTEROP_TESTS") is { Length: > 0 } value &&
        value is not ("0" or "false" or "False");

    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(!Enabled);
}
