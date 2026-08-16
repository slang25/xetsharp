using System.Net.Http.Headers;
using XetSharp.Hub;

namespace XetSharp.Tests;

/// <summary>
/// Interop against the real Hugging Face Hub, CAS service and CDN. Opt in with
/// <c>XETSHARP_LIVE_TESTS=1</c>; see <see cref="SkipWithoutLiveTestsAttribute"/>. No credentials
/// needed — both repositories are public, and the Hub issues anonymous Xet read tokens for them.
/// </summary>
/// <remarks>
/// Every check here compares the Xet path against something independent of it: the same bytes
/// fetched over plain HTTP from the Hub's LFS-compatible route, or the hashes the reference dataset
/// publishes. Agreement means the two implementations reconstructed the same file.
/// </remarks>
public class LiveDownloadTests
{
    private static readonly XetRepository ReferenceRepository = XetRepository.Dataset("xet-team/xet-spec-reference-files");

    private const string ReferenceFileName = "Electric_Vehicle_Population_Data_20250917.csv";

    /// <summary>A model file large enough to span several xorbs — nine, at the time of writing.</summary>
    private static readonly XetRepository ModelRepository = XetRepository.Model("openai-community/gpt2");

    private const string ModelFileName = "model.safetensors";

    [Test]
    [SkipWithoutLiveTests]
    public async Task Resolves_a_public_file_to_its_published_file_id()
    {
        using var client = new XetClient();

        var file = await client.GetFileInfoAsync(ReferenceRepository, ReferenceFileName);

        await Assert.That(file.FileId).IsEqualTo(MerkleHash.Parse(ReferenceFiles.FileHash));
        await Assert.That(file.Size).IsEqualTo(ReferenceFiles.FileLength);
        await Assert.That(file.Sha256).IsEqualTo(ReferenceFiles.Sha256);
    }

    /// <summary>
    /// The bytes the Xet path assembles must equal the bytes the Hub's ordinary download route
    /// serves for the same range — two independent paths to the same content.
    /// </summary>
    [Test]
    [SkipWithoutLiveTests]
    public async Task Downloads_a_range_matching_the_plain_http_download()
    {
        const long offset = 1_000_000;
        const int length = 250_000;

        using var client = new XetClient();
        using var destination = new MemoryStream();
        var result = await client.DownloadRangeAsync(ReferenceRepository, ReferenceFileName, destination, offset, length);

        var expected = await PlainHttpRangeAsync(ReferenceRepository, ReferenceFileName, offset, length);

        await Assert.That(result.BytesWritten).IsEqualTo(length);
        await Assert.That(destination.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A range straddling the boundary between two terms exercises the parts a single-xorb file
    /// never reaches: several fetches, a term boundary mid-range, and a non-zero
    /// <c>offset_into_first_range</c>.
    /// </summary>
    [Test]
    [SkipWithoutLiveTests]
    public async Task Downloads_a_range_that_spans_two_xorbs()
    {
        using var client = new XetClient();
        var file = await client.GetFileInfoAsync(ModelRepository, ModelFileName);
        var reconstruction = await client.Cas.GetReconstructionAsync(ModelRepository, file.FileId);
        await Assert.That(reconstruction.Terms.Count).IsGreaterThan(1);

        // Straddle the end of the first term.
        var boundary = reconstruction.Terms[0].UnpackedLength;
        var offset = boundary - 100_000;
        const int length = 200_000;

        using var destination = new MemoryStream();
        var result = await client.DownloadAsync(ModelRepository, file, destination, offset, length);
        var expected = await PlainHttpRangeAsync(ModelRepository, ModelFileName, offset, length);

        await Assert.That(result.BytesWritten).IsEqualTo(length);
        await Assert.That(destination.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The whole thing: 63 MB, 796 chunks, one xorb — reassembled, re-hashed to the file ID the
    /// reference implementation published, and checked against the SHA-256 the Hub records.
    /// </summary>
    [Test]
    [SkipWithoutLiveTests]
    public async Task Downloads_and_verifies_a_whole_public_file()
    {
        using var client = new XetClient();
        var path = Path.Combine(Path.GetTempPath(), $"xetsharp-live-{Guid.NewGuid():n}.csv");

        try
        {
            var result = await client.DownloadToFileAsync(ReferenceRepository, ReferenceFileName, path);

            await Assert.That(result.BytesWritten).IsEqualTo(ReferenceFiles.FileLength);
            await Assert.That(result.FileHash).IsEqualTo(MerkleHash.Parse(ReferenceFiles.FileHash));
            await Assert.That(result.Sha256).IsEqualTo(ReferenceFiles.Sha256);
            await Assert.That(new FileInfo(path).Length).IsEqualTo(ReferenceFiles.FileLength);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Fetches a byte range over the Hub's ordinary (non-Xet) download route.</summary>
    private static async Task<byte[]> PlainHttpRangeAsync(XetRepository repository, string path, long offset, int length)
    {
        using var http = new HttpClient();
        var url = $"https://huggingface.co/{repository.ResolvePrefix}{repository.Id}/resolve/{repository.Revision}/{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
}
