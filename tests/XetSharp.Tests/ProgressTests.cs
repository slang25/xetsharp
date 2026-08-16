using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Tests;

/// <summary>
/// Progress reporting, end to end through the real pipelines: what a caller watching an upload or a
/// download is told, and when.
/// </summary>
public class ProgressTests
{
    private static readonly XetRepository Repository = XetRepository.Model("acme/scratch");

    /// <summary>
    /// Big enough to cross the reporter's one-megabyte interval several times, and deliberately not
    /// a multiple of it: a transfer that happens to end on a reporting boundary would not notice a
    /// missing final report.
    /// </summary>
    private const int ContentSize = (5 * 1024 * 1024) + 3;

    [Test]
    public async Task Reports_upload_progress_up_to_the_files_total()
    {
        var content = TestData.SplitMix64Bytes(11, ContentSize);
        var (client, _) = NewClient();
        var reports = new RecordingProgress();

        await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)], reports);

        await Assert.That(reports.Reports).IsNotEmpty();
        await Assert.That(reports.Reports[0]).IsEqualTo(new XetProgress(0, ContentSize));
        await Assert.That(reports.Reports[^1]).IsEqualTo(new XetProgress(ContentSize, ContentSize));
        await Assert.That(reports.IsMonotonic).IsTrue();
    }

    /// <summary>
    /// The totals come from the files, so a batch reports against the sum of them rather than
    /// restarting at each one.
    /// </summary>
    [Test]
    public async Task Reports_one_running_total_across_a_batch()
    {
        var first = TestData.SplitMix64Bytes(12, ContentSize);
        var second = TestData.SplitMix64Bytes(13, ContentSize);
        var (client, _) = NewClient();
        var reports = new RecordingProgress();

        await client.UploadAsync(
            Repository,
            [XetUploadFile.FromBytes("first.bin", first), XetUploadFile.FromBytes("second.bin", second)],
            reports);

        await Assert.That(reports.Reports[^1]).IsEqualTo(new XetProgress(2 * ContentSize, 2 * ContentSize));
        await Assert.That(reports.IsMonotonic).IsTrue();
    }

    /// <summary>
    /// A stream that cannot say how long it is leaves the total unknown rather than guessing at one:
    /// a progress bar is better absent than wrong.
    /// </summary>
    [Test]
    public async Task Reports_no_total_for_an_unmeasurable_stream()
    {
        var content = TestData.SplitMix64Bytes(14, ContentSize);
        var (client, _) = NewClient();
        var reports = new RecordingProgress();

        using var stream = new UnseekableStream(content);
        await client.UploadAsync(Repository, [XetUploadFile.FromStream("data.bin", stream)], reports);

        await Assert.That(reports.Reports.All(report => report.TotalBytes is null)).IsTrue();
        await Assert.That(reports.Reports[^1].BytesTransferred).IsEqualTo((long)ContentSize);
    }

    [Test]
    public async Task Reports_download_progress_up_to_the_file_size()
    {
        var content = TestData.SplitMix64Bytes(15, ContentSize);
        var (client, _) = NewClient();
        var uploaded = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        var reports = new RecordingProgress();

        using var destination = new MemoryStream();
        await client.DownloadAsync(Repository, uploaded.Files.Single().FileId.ToString(), destination, reports);

        await Assert.That(reports.Reports[0]).IsEqualTo(new XetProgress(0, ContentSize));
        await Assert.That(reports.Reports[^1]).IsEqualTo(new XetProgress(ContentSize, ContentSize));
        await Assert.That(reports.Reports.Count).IsGreaterThan(2);
        await Assert.That(reports.IsMonotonic).IsTrue();
    }

    /// <summary>A range download reports against the slice asked for, not the whole file.</summary>
    [Test]
    public async Task Reports_a_range_download_against_the_slice()
    {
        var content = TestData.SplitMix64Bytes(16, ContentSize);
        var (client, _) = NewClient();
        var uploaded = await client.UploadAsync(Repository, [XetUploadFile.FromBytes("data.bin", content)]);
        var reports = new RecordingProgress();

        using var destination = new MemoryStream();
        await client.DownloadRangeAsync(
            Repository, uploaded.Files.Single().FileId.ToString(), destination, offset: 1_000, length: 2_000_000, progress: reports);

        await Assert.That(reports.Reports[^1]).IsEqualTo(new XetProgress(2_000_000, 2_000_000));
    }

    [Test]
    public async Task A_fraction_needs_a_total()
    {
        await Assert.That(new XetProgress(50, 100).Fraction).IsEqualTo(0.5);
        await Assert.That(new XetProgress(50, null).Fraction).IsNull();
        await Assert.That(new XetProgress(50, 0).Fraction).IsNull();
    }

    private static (XetClient Client, FakeCas Cas) NewClient()
    {
        var cas = new FakeCas();
        var client = new XetClient(new XetClientOptions
        {
            HubUrl = new Uri("https://hub.invalid"),
            UseAmbientCredentials = false,
            HttpClient = new HttpClient(new FakeHttpHandler(cas.Handler)),
        });

        return (client, cas);
    }

    private sealed class RecordingProgress : IProgress<XetProgress>
    {
        public List<XetProgress> Reports { get; } = [];

        public bool IsMonotonic => Reports.Zip(Reports.Skip(1)).All(pair => pair.Second.BytesTransferred >= pair.First.BytesTransferred);

        public void Report(XetProgress value) => Reports.Add(value);
    }

    /// <summary>A stream that will not say how long it is, standing in for a pipe or a network read.</summary>
    private sealed class UnseekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
