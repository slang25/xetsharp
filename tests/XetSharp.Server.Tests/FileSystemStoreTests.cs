using System.Collections.Immutable;
using XetSharp.Hashing;
using XetSharp.Hub;
using XetSharp.Server.Storage;
using XetSharp.Upload;

namespace XetSharp.Server.Tests;

/// <summary>
/// The directory-backed store: the same round trip as memory, and then the part memory cannot
/// do — a new store over the same directory still has everything.
/// </summary>
public class FileSystemStoreTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/persisted");

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "xetsharp-server-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task Round_trips_and_survives_a_restart()
    {
        var root = NewRoot();
        try
        {
            var content = TestData.WithEligibleChunk(40, 1_500_000);
            Upload.XetUploadResult upload;

            var store = new FileSystemXetStore(root);
            await using (var host = await TestXetServer.StartAsync(store, store, new XetServerOptions { SigningKey = new byte[32] }))
            {
                using var client = host.CreateClient(options: new XetClientOptions { Upload = new XetUploadOptions { MaxXorbBytes = 500_000 } });
                upload = await client.UploadAndCommitAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)], "Add");
                await Assert.That(upload.XorbCount).IsGreaterThan(1);
            }

            await Assert.That(Directory.EnumerateFiles(Path.Combine(root, "xorbs"), "*.index", SearchOption.AllDirectories).Count()).IsEqualTo(upload.XorbCount);
            await Assert.That(Directory.Exists(Path.Combine(root, "chunks"))).IsTrue();

            var reopened = new FileSystemXetStore(root);
            await using (var host = await TestXetServer.StartAsync(reopened, reopened, new XetServerOptions { SigningKey = new byte[32] }))
            {
                using var client = host.CreateClient();
                using var downloaded = new MemoryStream();
                var download = await client.DownloadAsync(Repository, "data.bin", downloaded);
                await Assert.That(downloaded.ToArray()).IsEquivalentTo(content, CollectionOrdering.Matching);
                await Assert.That(download.FileHash).IsEqualTo(upload.Files.Single().FileId);

                // The global index came back too: a fresh upload of the same bytes dedupes.
                var again = await client.UploadAsync(XetRepository.Model("acme/again"), [XetUploadFile.FromBytes("data.bin", content)]);
                await Assert.That(again.DeduplicatedBytes).IsGreaterThan(0);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Concurrent_commits_from_the_same_parent_land_exactly_once()
    {
        var root = NewRoot();
        try
        {
            var store = new FileSystemXetStore(root);
            await using var host = await TestXetServer.StartAsync(store, store);
            using var client = host.CreateClient();
            await CommitRaces.ExactlyOneLandsAsync(client, Repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Xorb_index_codec_round_trips()
    {
        var chunks = Enumerable.Range(0, 5)
            .Select(i => new StoredChunk(XetHashes.ChunkHash([(byte)i]), 1000 + i, i * 2000L, 1500 + i))
            .ToImmutableArray();
        var xorb = new StoredXorb(XetHashes.ChunkHash("xorb"u8), 123_456, chunks);

        var decoded = StoredXorbCodec.Decode(xorb.Hash, StoredXorbCodec.Encode(xorb));

        await Assert.That(decoded).IsEqualTo(xorb);
    }

    [Test]
    public async Task File_record_codec_round_trips()
    {
        var file = new StoredFile(
            XetHashes.ChunkHash("file"u8),
            3000,
            MerkleHash.FromHexOrder(System.Security.Cryptography.SHA256.HashData("file"u8)),
            [new StoredTerm(XetHashes.ChunkHash("a"u8), 0, 3, 1000), new StoredTerm(XetHashes.ChunkHash("b"u8), 2, 9, 2000)]);

        var decoded = StoredFileCodec.Decode(StoredFileCodec.Encode(file));

        await Assert.That(decoded).IsEqualTo(file);
    }

    [Test]
    public async Task Revision_codec_round_trips()
    {
        var revision = new RepositoryRevision(
            "abc123",
            new Dictionary<string, RepositoryFile>
            {
                ["a/b.bin"] = new("a/b.bin", XetHashes.ChunkHash("a"u8), 10, new string('1', 64)),
                ["c.bin"] = new("c.bin", XetHashes.ChunkHash("c"u8), 20, new string('2', 64)),
            });

        var decoded = RepositoryRevisionCodec.Decode(RepositoryRevisionCodec.Encode(revision));

        await Assert.That(decoded.CommitId).IsEqualTo("abc123");
        await Assert.That(decoded.Files["a/b.bin"]).IsEqualTo(revision.Files["a/b.bin"]);
        await Assert.That(decoded.Files["c.bin"]).IsEqualTo(revision.Files["c.bin"]);
    }
}
