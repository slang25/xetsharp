using System.Net;
using XetSharp.Hub;
using XetSharp.Upload;

namespace XetSharp.Server.Tests;

/// <summary>
/// Commits racing from the same parent, which every repository store has to settle the same way:
/// exactly one lands, and the rest are told the branch moved.
/// </summary>
internal static class CommitRaces
{
    public static async Task ExactlyOneLandsAsync(XetClient client, XetRepository repository, int racers = 8)
    {
        var start = await client.UploadAndCommitAsync(repository, [XetUploadFile.FromBytes("racing.bin", TestData.SplitMix64Bytes(77, 20_000))], "Start");
        var parent = start.Commit!.CommitOid;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, racers).Select(async racer =>
        {
            try
            {
                await client.CommitAsync(repository, new XetCommitRequest { Summary = $"Racer {racer}", DeletedFiles = ["racing.bin"], ParentCommit = parent });
                return HttpStatusCode.OK;
            }
            catch (XetApiException exception)
            {
                return exception.StatusCode;
            }
        }));

        await Assert.That(outcomes.Count(status => status == HttpStatusCode.OK)).IsEqualTo(1).Because(string.Join(", ", outcomes));
        await Assert.That(outcomes.Count(status => status == HttpStatusCode.PreconditionFailed)).IsEqualTo(racers - 1).Because(string.Join(", ", outcomes));
    }
}
