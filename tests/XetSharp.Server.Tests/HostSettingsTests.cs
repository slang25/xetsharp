using XetSharp.Server.Host;

namespace XetSharp.Server.Tests;

/// <summary>
/// The command line the single binary is driven by. Asking for help is a success and a mistake is
/// not, which matters beyond tidiness: CI smoke-tests the published binary with <c>--help</c>.
/// </summary>
public class HostSettingsTests
{
    [Test]
    [Arguments("--help")]
    [Arguments("-h")]
    public async Task Help_is_asked_for_rather_than_mistaken(string flag)
    {
        var settings = HostSettings.Parse([flag], out var helpRequested);

        await Assert.That(helpRequested).IsTrue();
        await Assert.That(settings).IsNull();
    }

    [Test]
    public async Task An_unknown_option_is_a_mistake()
    {
        var settings = HostSettings.Parse(["--nonsense"], out var helpRequested);

        await Assert.That(helpRequested).IsFalse();
        await Assert.That(settings).IsNull();
    }

    [Test]
    public async Task Options_are_read_from_the_command_line()
    {
        var settings = HostSettings.Parse(
            ["--store", "/tmp/xet", "--urls", "http://127.0.0.1:8080", "--hub-token", "one", "--hub-token", "two", "--no-anonymous-writes"],
            out var helpRequested);

        await Assert.That(helpRequested).IsFalse();
        await Assert.That(settings).IsNotNull();
        await Assert.That(settings!.Store).IsEqualTo("/tmp/xet");
        await Assert.That(settings.Urls).IsEqualTo("http://127.0.0.1:8080");
        await Assert.That(settings.Options.HubTokens).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);
        await Assert.That(settings.Options.AllowAnonymousWrites).IsFalse();
        await Assert.That(settings.Options.AllowAnonymousReads).IsTrue();
    }
}
