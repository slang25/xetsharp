using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Tests;

/// <summary>
/// The upload half against the real Hugging Face Hub and CAS service. Opt in by naming a scratch
/// repository you have write access to; see <see cref="SkipWithoutLiveUploadAttribute"/>.
/// </summary>
/// <remarks>
/// Every test here cleans up after itself with a deleting commit, and every file it writes carries a
/// fresh GUID in its name and its contents — so a run leaves nothing behind, and nothing it uploads
/// can deduplicate against anything that already existed.
/// </remarks>
public class LiveUploadTests
{
    /// <summary>
    /// Upload, commit, download, compare. The download goes back through the resolve endpoint, so a
    /// pass means the Hub agrees the file is there, agrees on its ID, and can serve it back.
    /// </summary>
    [Test]
    [SkipWithoutLiveUpload]
    public async Task Uploads_commits_and_downloads_a_file()
    {
        var repository = SkipWithoutLiveUploadAttribute.Repository!;
        var name = $"xetsharp-live-{Guid.NewGuid():n}.bin";
        var content = UniqueContent(3_000_000);

        using var client = new XetClient();
        try
        {
            var result = await client.UploadAndCommitAsync(
                repository,
                [XetUploadFile.FromBytes(name, content)],
                $"Add {name} (XetSharp live test)");

            var uploaded = result.Files.Single();
            await Assert.That(uploaded.Sha256).IsEqualTo(FakeCas.Sha256Of(content));
            await Assert.That(result.XorbCount).IsGreaterThan(0);
            await Assert.That(result.Commit!.CommitOid).IsNotNull();

            var file = await client.GetFileInfoAsync(repository, name);
            await Assert.That(file.FileId).IsEqualTo(uploaded.FileId);
            await Assert.That(file.Size).IsEqualTo(content.Length);

            using var downloaded = new MemoryStream();
            var download = await client.DownloadAsync(repository, name, downloaded);

            await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
            await Assert.That(download.FileHash).IsEqualTo(uploaded.FileId);
        }
        finally
        {
            await DeleteAsync(client, repository, name);
        }
    }

    /// <summary>
    /// Uploading the same bytes a second time should move nothing: the service already holds the
    /// chunks, so the global index answers for them and no xorb is sent.
    /// </summary>
    [Test]
    [SkipWithoutLiveUpload]
    public async Task Deduplicates_a_second_upload_of_the_same_content()
    {
        var repository = SkipWithoutLiveUploadAttribute.Repository!;
        var first = $"xetsharp-live-{Guid.NewGuid():n}.bin";
        var second = $"xetsharp-live-{Guid.NewGuid():n}.bin";

        // Big enough that the default one-in-1024 sample is certain to include an eligible chunk:
        // 1024 chunks of ~64 KiB is about 64 MiB, so this is a few times that.
        var content = UniqueContent(200_000_000);

        using var client = new XetClient();
        try
        {
            var original = await client.UploadAndCommitAsync(
                repository, [XetUploadFile.FromBytes(first, content)], $"Add {first} (XetSharp live test)");
            await Assert.That(original.XorbCount).IsGreaterThan(1);

            var repeat = await client.UploadAndCommitAsync(
                repository, [XetUploadFile.FromBytes(second, content)], $"Add {second} (XetSharp live test)");

            await Assert.That(repeat.Files.Single().FileId).IsEqualTo(original.Files.Single().FileId);
            await Assert.That(repeat.GlobalDeduplicationQueries).IsGreaterThan(0);
            await Assert.That(repeat.DeduplicatedBytes).IsGreaterThan(0L);
            await Assert.That(repeat.UploadedBytes).IsLessThan(original.UploadedBytes);
        }
        finally
        {
            await DeleteAsync(client, repository, first, second);
        }
    }

    /// <summary>
    /// Bytes that cannot have been uploaded before: a GUID, then pseudo-random data seeded from it.
    /// </summary>
    private static byte[] UniqueContent(int length)
    {
        var seed = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray());
        var content = TestData.SplitMix64Bytes(seed, length);
        Guid.NewGuid().ToByteArray().CopyTo(content, 0);
        return content;
    }

    private static async Task DeleteAsync(XetClient client, XetRepository repository, params string[] paths)
    {
        try
        {
            await client.CommitAsync(repository, new XetCommitRequest
            {
                Summary = "Remove XetSharp live test files",
                DeletedFiles = paths,
            });
        }
        catch (XetException exception)
        {
            // The upload that put them there is what the test is about; a failed tidy-up should not
            // hide why it failed.
            Console.WriteLine($"Could not remove {string.Join(", ", paths)}: {exception.Message}");
        }
    }
}
