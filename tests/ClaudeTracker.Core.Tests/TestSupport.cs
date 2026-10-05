using System.Globalization;
using System.Runtime.CompilerServices;
using ClaudeTracker.Core;

namespace ClaudeTracker.Core.Tests;

/// <summary>Reads this repo's local copies of the workspace's canonical test vectors.</summary>
internal static class Fixture
{
    public static string PathOf(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(PathOf(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));
}

internal static class TestSetup
{
    /// <summary>
    /// Pins the suite to English strings and an invariant-like culture, so a title such as
    /// "5-Hour Window" or a formatted number never depends on the machine running the tests.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        L.LanguageOverride = "en";
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
}

/// <summary>Short spellings for instants, so ported test cases read like their Swift originals.</summary>
internal static class T
{
    /// <summary>A fixed reference instant for tests that only need relative times.</summary>
    public static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    /// <summary>An ISO 8601 string for an instant, the way the API sends <c>resets_at</c>.</summary>
    public static string Iso(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
