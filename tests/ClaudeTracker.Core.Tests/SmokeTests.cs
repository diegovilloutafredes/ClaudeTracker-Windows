using ClaudeTracker.Core;

namespace ClaudeTracker.Core.Tests;

/// <summary>
/// First checks of the port's foundations: localization, timestamp parsing, the live payload
/// vectors, and the release-signing key. The full ports of the macOS suites sit beside this file.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void PrintfKeysBecomeCompositeFormats()
    {
        Assert.Equal("{0} fills in {1} min at {2}", L.ToCompositeFormat("%@ fills in %d min at %@"));
        Assert.Equal("actual {0}%  expected {1}%  {2}", L.ToCompositeFormat("actual %d%%  expected %d%%  %@"));
        Assert.Equal("{0}%", L.ToCompositeFormat("%lld%%"));
        Assert.Equal("{1} then {0}", L.ToCompositeFormat("%2$@ then %1$@"));
        Assert.Equal("{{literal}} 100%", L.ToCompositeFormat("{literal} 100%"));
    }

    [Fact]
    public void SpanishComesFromTheSharedCatalog()
    {
        // Looked up by language, never by switching the process-wide override: other test
        // classes run in parallel and assert English titles.
        Assert.Equal("Uso", L.Lookup("es", "Usage"));
        Assert.NotEqual("7-Day %@", L.Lookup("es", "7-Day %@"));
        Assert.Contains("Fable", L.Format("es", "7-Day %@", "Fable"));
        Assert.Equal("An untranslated key", L.Lookup("es", "An untranslated key"));
        Assert.Equal("Abrir", L.Lookup("es", "Open")); // a Windows-only string
    }

    [Theory]
    [InlineData("2026-09-10T15:00:00Z", 2026, 9, 10, 15, 0, 0, 0)]
    [InlineData("2026-09-10T15:00:00.500Z", 2026, 9, 10, 15, 0, 0, 500)]
    [InlineData("2026-09-23T20:59:59.790000+00:00", 2026, 9, 23, 20, 59, 59, 790)]
    [InlineData("2026-09-10T12:00:00-03:00", 2026, 9, 10, 15, 0, 0, 0)]
    public void IsoDateParsesWhatTheApiSends(string text, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        var expected = new DateTimeOffset(y, mo, d, h, mi, s, ms, TimeSpan.Zero);
        Assert.Equal(expected, IsoDate.Parse(text)?.ToUniversalTime());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-09-10")]
    [InlineData("2026-09-10T15:00:00")]
    [InlineData("2026-13-10T15:00:00Z")]
    public void IsoDateRejectsAnythingElse(string? text) => Assert.Null(IsoDate.Parse(text));

    [Theory]
    [InlineData("live-usage-2026-09-10.json")]
    [InlineData("live-usage-2026-09-21.json")]
    public void LivePayloadsDecode(string vector)
    {
        var response = UsageResponse.Parse(Fixture.Text(vector));
        Assert.NotNull(response.FiveHour);
        Assert.NotNull(response.SevenDay);
        Assert.NotEmpty(response.TrackedWindows);
        Assert.All(response.TrackedWindows, w => Assert.InRange(w.Window.Utilization, 0, 200));
    }

    [Fact]
    public void TheReleaseSignatureDoesNotVerifyAChangedFile()
    {
        // The positive check against the real key is the ported
        // EmbeddedPublicKeyMatchesTheReleaseSigningKey; this is its other half.
        var text = Fixture.Bytes("signing-check.txt");
        var signature = Fixture.Bytes("signing-check.txt.sig");
        text[0] ^= 1;
        Assert.False(Updates.VerifyUpdateSignature(text, signature, Updates.SigningPublicKey));
    }
}
