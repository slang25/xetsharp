using XetSharp.Hashing;
using XetSharp.Upload;
using XetSharp.Xorbs;

namespace XetSharp.Tests;

/// <summary>
/// The packer that decides what goes in a xorb. Its one hard obligation is the CAS service's 64 MiB
/// ceiling on a serialized xorb: exceeding it means an upload the service refuses. Its second is
/// that compressing chunks in parallel changes nothing about the bytes that come out.
/// </summary>
public class XorbBuilderTests
{
    /// <summary>
    /// Filled with data that does not compress — the worst case, where every chunk is stored as-is
    /// and pays for its 8-byte header — a full xorb still serializes to under the limit.
    /// </summary>
    [Test]
    public async Task Stays_under_the_size_limit_on_data_that_will_not_compress()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);
        var chunk = TestData.SplitMix64Bytes(1, XorbChunkHeader.MaxUncompressedSize);

        while (builder.CanAdd(chunk.Length))
        {
            await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
        }

        var packed = await builder.BuildAsync();
        await Assert.That((uint)packed.Serialized.Length).IsGreaterThan(packed.Info.TotalUncompressedBytes);
        await Assert.That(packed.Serialized.Length).IsLessThanOrEqualTo(XorbSerializer.MaxSerializedSize);
    }

    /// <summary>
    /// The chunk-count cap is what makes the size guarantee a fixed number rather than something to
    /// discover a chunk too late.
    /// </summary>
    [Test]
    public async Task Stops_at_the_chunk_count_cap()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);
        var chunk = new byte[8];

        while (builder.CanAdd(chunk.Length))
        {
            await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
        }

        await Assert.That(builder.ChunkCount).IsEqualTo(XorbBuilder.MaxChunks);
        await Assert.That(async () => await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk)))
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// What the builder produces is what the shard has to say about it: the xorb's hash over its
    /// chunks, the raw total, the serialized length, and each chunk's offset in the uncompressed
    /// stream.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task Describes_what_it_packed(int parallelism)
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, parallelism);
        var chunks = Enumerable.Range(0, 5).Select(i => TestData.SplitMix64Bytes((ulong)i + 1, 10_000 + i)).ToArray();
        foreach (var chunk in chunks)
        {
            await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
        }

        var packed = await builder.BuildAsync();

        await Assert.That(packed.Hash).IsEqualTo(
            XetHashes.XorbHash([.. chunks.Select(chunk => (XetHashes.ChunkHash(chunk), (ulong)chunk.Length))]));
        await Assert.That(packed.Info.XorbHash).IsEqualTo(packed.Hash);
        await Assert.That(packed.Info.SerializedLength).IsEqualTo((uint)packed.Serialized.Length);
        await Assert.That(packed.Info.TotalUncompressedBytes).IsEqualTo((uint)chunks.Sum(chunk => chunk.Length));

        var offset = 0u;
        for (var i = 0; i < chunks.Length; i++)
        {
            await Assert.That(packed.Info.Chunks[i].ByteRangeStart).IsEqualTo(offset);
            await Assert.That(packed.Info.Chunks[i].UnpackedLength).IsEqualTo((uint)chunks[i].Length);
            offset += (uint)chunks[i].Length;
        }

        await Assert.That(XorbSerializer.Deserialize(packed.Serialized).Select(chunk => chunk.Length))
            .IsEquivalentTo(chunks.Select(chunk => chunk.Length), CollectionOrdering.Matching);
    }

    /// <summary>Building empties the builder, so the next xorb starts at index zero and offset zero.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task Starts_over_after_building(int parallelism)
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, parallelism);
        var chunk = TestData.SplitMix64Bytes(9, 5_000);

        await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
        await builder.BuildAsync();

        await Assert.That(builder.IsEmpty).IsTrue();
        await Assert.That(await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk))).IsEqualTo(0);
        await Assert.That((await builder.BuildAsync()).Info.Chunks.Single().ByteRangeStart).IsEqualTo(0u);
    }

    [Test]
    public async Task Refuses_to_build_a_xorb_with_no_chunks()
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes);

        await Assert.That(async () => await builder.BuildAsync()).Throws<InvalidOperationException>();
    }

    /// <summary>
    /// The whole point of the parallel path: it may compress chunks in any order it likes, but the
    /// xorb it produces has to be indistinguishable from the one the inline path produces — the same
    /// bytes, so the same hash, so the same object in the CAS. Data of three shapes, because which
    /// compression scheme wins differs between them and a race would show up as a scheme applied to
    /// the wrong chunk.
    /// </summary>
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(64)]
    public async Task Packs_the_same_bytes_however_many_chunks_compress_at_once(int parallelism)
    {
        var chunks = MixedChunks();

        var serial = await PackAsync(chunks, 1);
        var parallelPacked = await PackAsync(chunks, parallelism);

        await Assert.That(parallelPacked.Hash).IsEqualTo(serial.Hash);
        await Assert.That(parallelPacked.Serialized).IsEquivalentTo(serial.Serialized, CollectionOrdering.Matching);
        await Assert.That(parallelPacked.Info.SerializedLength).IsEqualTo(serial.Info.SerializedLength);
        await Assert.That(parallelPacked.Info.TotalUncompressedBytes).IsEqualTo(serial.Info.TotalUncompressedBytes);
        await Assert.That(parallelPacked.Info.Chunks).IsEquivalentTo(serial.Info.Chunks, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The other half of the parallel path's contract, and the half byte-identity cannot see: the
    /// setting is a cap on how many chunks compress at once, so a window that let twice that many run
    /// would produce exactly the same xorb while taking twice the cores it was allowed. Compression is
    /// replaced with a delegate that holds every chunk until the test lets it go, so the builder is
    /// pushed against its window rather than raced against it and the count it reaches is the count it
    /// permits, not whatever the thread pool happened to start.
    /// </summary>
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task Compresses_no_more_chunks_at_once_than_it_was_told_to(int parallelism)
    {
        var sync = new Lock();
        var inFlight = 0;
        var peak = 0;
        var started = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Compresses on the calling thread and only then waits, so what the counter measures is the
        // builder's window and never the thread pool's willingness to run the work.
        Task<CompressedChunk> CompressAsync(ReadOnlyMemory<byte> chunk)
        {
            lock (sync)
            {
                peak = Math.Max(peak, ++inFlight);
            }

            started.Release();
            return HoldAsync(XorbSerializer.CompressChunk(chunk.Span));
        }

        async Task<CompressedChunk> HoldAsync(CompressedChunk compressed)
        {
            await release.Task;
            lock (sync)
            {
                inFlight--;
            }

            return compressed;
        }

        var chunks = MixedChunks();
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, parallelism, CompressAsync);
        var packing = Task.Run(async () =>
        {
            foreach (var chunk in chunks)
            {
                await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
            }

            return await builder.BuildAsync();
        });

        var startedWithinWindow = 0;
        while (startedWithinWindow < parallelism && await started.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            startedWithinWindow++;
        }

        // The window is full and nothing has been appended, so a builder that respects it cannot have
        // started another chunk — and one that does not will have started it long before this elapses.
        // Observed before anything is asserted: an assertion that threw here would leave the delegates
        // waiting on a gate nobody opens, and disposing the builder waits on them in turn.
        var startedBeyondWindow = await started.WaitAsync(TimeSpan.FromMilliseconds(250));

        release.SetResult();
        var packed = await packing;

        await Assert.That(startedWithinWindow).IsEqualTo(parallelism);
        await Assert.That(startedBeyondWindow).IsFalse();
        await Assert.That(peak).IsEqualTo(parallelism);
        await Assert.That(packed.Serialized).IsEquivalentTo(
            (await PackAsync(chunks, 1)).Serialized, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A chunk still compressing when a xorb is sealed has to be waited for, not dropped: the danger
    /// with a bounded window is a xorb that seals with its last few records missing.
    /// </summary>
    [Test]
    public async Task Waits_for_the_chunks_still_compressing_when_it_seals()
    {
        var chunks = MixedChunks();

        var packed = await PackAsync(chunks, 8);

        var round = XorbSerializer.Deserialize(packed.Serialized);
        await Assert.That(round.Count).IsEqualTo(chunks.Length);
        for (var i = 0; i < chunks.Length; i++)
        {
            await Assert.That(round[i]).IsEquivalentTo(chunks[i], CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// Disposing with chunks still in flight has to return their buffers and swallow their failures
    /// rather than leaving an upload that already went wrong to fail a second time on the way out.
    /// </summary>
    [Test]
    public async Task Disposing_mid_flight_is_quiet()
    {
        var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, 8);
        foreach (var chunk in MixedChunks())
        {
            await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
        }

        await Assert.That(builder.Dispose).ThrowsNothing();
    }

    /// <summary>
    /// Chunks of the three kinds that take different paths through the serializer: text that plain
    /// LZ4 wins on, float32 weights that byte grouping wins on, and random bytes that neither helps,
    /// interleaved so a misordered record cannot go unnoticed.
    /// </summary>
    private static byte[][] MixedChunks()
    {
        var chunks = new List<byte[]>();
        for (var i = 0; i < 30; i++)
        {
            chunks.Add((i % 3) switch
            {
                0 => TestData.SplitMix64Bytes((ulong)i + 1, 40_000 + i),
                1 => Repeating(i, 50_000 + i),
                _ => Weights(i, 60_000 + i),
            });
        }

        return [.. chunks];
    }

    private static byte[] Repeating(int seed, int length)
    {
        var text = new byte[length];
        for (var i = 0; i < length; i++)
        {
            text[i] = (byte)('a' + ((i + seed) % 16));
        }

        return text;
    }

    /// <summary>Float32s drifting slowly, where the top bytes barely change — what byte grouping is for.</summary>
    private static byte[] Weights(int seed, int length)
    {
        var weights = new byte[length];
        for (var i = 0; i + 4 <= length; i += 4)
        {
            BitConverter.TryWriteBytes(weights.AsSpan(i), 0.5f + (((i / 4) + seed) % 97) * 1e-4f);
        }

        return weights;
    }

    private static async Task<PackedXorb> PackAsync(byte[][] chunks, int parallelism)
    {
        using var builder = new XorbBuilder(XorbBuilder.MaxUncompressedBytes, parallelism);
        foreach (var chunk in chunks)
        {
            await builder.AddAsync(chunk, XetHashes.ChunkHash(chunk));
        }

        return await builder.BuildAsync();
    }
}
