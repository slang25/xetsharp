namespace XetSharp.Tests;

/// <summary>
/// Skips a test unless <c>XETSHARP_REFERENCE_DIR</c> points at a local copy of the
/// xet-team/xet-spec-reference-files dataset. Those artefacts include a 63 MB CSV and a 14 MB xorb,
/// too large to vendor, so tests that need them stay off by default:
/// <code>hf download xet-team/xet-spec-reference-files --repo-type dataset --local-dir &lt;dir&gt;</code>
/// </summary>
public sealed class SkipWithoutReferenceDatasetAttribute()
    : SkipAttribute("XETSHARP_REFERENCE_DIR is not set to a local copy of the xet-spec-reference-files dataset.")
{
    /// <summary>The dataset directory, or null when the tests using it should be skipped.</summary>
    public static string? Directory
    {
        get
        {
            var directory = Environment.GetEnvironmentVariable("XETSHARP_REFERENCE_DIR");
            return string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory) ? null : directory;
        }
    }

    public static byte[] Read(string name) => File.ReadAllBytes(PathTo(name));

    public static string PathTo(string name) =>
        Path.Combine(Directory ?? throw new InvalidOperationException("Reference dataset is unavailable."), name);

    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(Directory is null);
}
