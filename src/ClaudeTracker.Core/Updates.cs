using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace ClaudeTracker.Core;

/// <summary>A newer version discovered via the GitHub Releases API.</summary>
/// <param name="DownloadUrl">
/// The installer asset. Null unless the release also carries the installer's signature: an
/// unsigned installer is offered only as a manual download from the release page.
/// </param>
/// <param name="SignatureUrl">The installer's detached Ed25519 signature, set with <paramref name="DownloadUrl"/>.</param>
public sealed record UpdateInfo(string Version, Uri ReleaseUrl, Uri? DownloadUrl, Uri? SignatureUrl = null);

/// <summary>
/// The pure half of the in-app update flow — the Windows twins of the helpers in the macOS app's
/// UpdateService.swift, sharing its signing key.
/// </summary>
public static partial class Updates
{
    /// <summary>
    /// The release asset the Windows app installs from, on the Windows repo's own releases.
    /// Matched by exact name, never by extension, so no other file on a release can be taken
    /// for the installer.
    /// </summary>
    public const string InstallerAssetName = "ClaudeTracker-Setup.exe";

    /// <summary>
    /// Public half of the release-signing key, whose private half lives only in the maintainer's
    /// Keychain (<c>scripts/update-signing.swift</c>). The same key signs the Mac zip. Compiled
    /// in rather than read from a file beside the executable: a tampered install is exactly what
    /// it defends against.
    /// </summary>
    public static ReadOnlySpan<byte> SigningPublicKey => Convert.FromBase64String("rnHlUrHGhtrUIQAgZxvIHG5vO1kvTZNxRDR+KTFmcIg=");

    /// <summary>Failed installs of one release that auto-install tolerates before it stops retrying.</summary>
    public const int MaxAutoInstallAttempts = 3;

    [GeneratedRegex(@"^\d+(\.\d+)*$")]
    private static partial Regex PlainVersion();

    /// <summary>
    /// True when <paramref name="remote"/> is a higher version than <paramref name="current"/>.
    /// Digit runs compare as numbers, so "1.10.0" &gt; "1.9.0".
    /// </summary>
    public static bool IsNewerVersion(string remote, string current) => CompareNumeric(remote, current) > 0;

    /// <summary>Ordinal comparison in which runs of digits compare by numeric value.</summary>
    internal static int CompareNumeric(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var runA = a.AsSpan(startA, i - startA).TrimStart('0');
                var runB = b.AsSpan(startB, j - startB).TrimStart('0');
                if (runA.Length != runB.Length) return runA.Length < runB.Length ? -1 : 1;
                var order = runA.SequenceCompareTo(runB);
                if (order != 0) return order < 0 ? -1 : 1;
            }
            else
            {
                if (a[i] != b[j]) return a[i] < b[j] ? -1 : 1;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>
    /// Parses the GitHub <c>releases?per_page=N</c> JSON payload.
    ///
    /// Returns the newest stable release as an <see cref="UpdateInfo"/> when it is strictly newer
    /// than <paramref name="currentVersion"/> (null otherwise), plus all <c>published_at</c>
    /// dates for the adaptive check interval. A malformed payload (e.g. GitHub's rate-limit
    /// error object instead of an array) yields <c>(null, [])</c>.
    /// </summary>
    public static (UpdateInfo? Update, IReadOnlyList<DateTimeOffset> ReleaseDates) ParseGitHubReleases(
        string json, string currentVersion, string assetName = InstallerAssetName)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return (null, []); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return (null, []);
            var releases = document.RootElement.EnumerateArray().ToList();
            // Anything but a list of release objects is not the payload this expects.
            if (releases.Any(r => r.ValueKind != JsonValueKind.Object)) return (null, []);

            // GitHub sends whole seconds; a date in any other shape is left out, as on the Mac.
            var dates = releases
                .Select(r => IsoDate.Parse(Lenient.String(r, "published_at"), allowFraction: false))
                .OfType<DateTimeOffset>()
                .ToList();

            static string Version(JsonElement release)
            {
                var tag = Lenient.String(release, "tag_name") ?? "";
                return tag.StartsWith('v') ? tag[1..] : tag;
            }

            // Pre-releases and drafts must never reach auto-update users — a beta published for
            // testing would otherwise install itself within a day. A tag that is not a plain
            // dotted version is passed over the same way: it is not a release of this app, and
            // compared as text it would read as "newer" by its first letter.
            var latest = releases.FirstOrDefault(r =>
                Lenient.Bool(r, "prerelease") != true && Lenient.Bool(r, "draft") != true && PlainVersion().IsMatch(Version(r)));
            if (latest.ValueKind != JsonValueKind.Object
                || Lenient.String(latest, "html_url") is not { } htmlUrl
                || !Uri.TryCreate(htmlUrl, UriKind.Absolute, out var releaseUrl)) return (null, dates);

            var remote = Version(latest);
            if (!IsNewerVersion(remote, currentVersion)) return (null, dates);

            Uri? AssetUrl(string name)
            {
                if (!latest.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.Object && Lenient.String(asset, "name") == name
                        && Uri.TryCreate(Lenient.String(asset, "browser_download_url"), UriKind.Absolute, out var url)) return url;
                }
                return null;
            }
            // In-app install needs the installer's signature; without one the release page is the only path.
            if (AssetUrl(assetName) is not { } installer || AssetUrl(assetName + ".sig") is not { } signature)
            {
                return (new UpdateInfo(remote, releaseUrl, null), dates);
            }
            return (new UpdateInfo(remote, releaseUrl, installer, signature), dates);
        }
    }

    /// <summary>
    /// <paramref name="update"/> without its in-app install when it is the release whose
    /// signature was rejected this session: Install would only fail the same way, so the UI
    /// falls back to the release page's Download link.
    /// </summary>
    public static UpdateInfo InstallableUpdate(UpdateInfo update, string? signatureRejectedVersion) =>
        update.Version == signatureRejectedVersion ? new UpdateInfo(update.Version, update.ReleaseUrl, null) : update;

    /// <summary>
    /// True when <paramref name="signature"/> is a valid Ed25519 signature of
    /// <paramref name="data"/> by the raw 32-byte <paramref name="publicKey"/>. Malformed keys
    /// or signatures are simply invalid.
    /// </summary>
    public static bool VerifyUpdateSignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != Ed25519.SignatureSize || publicKey.Length != Ed25519.PublicKeySize) return false;
        try
        {
            var message = data.ToArray();
            return Ed25519.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, message, 0, message.Length);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The failure count after one more failed install of <paramref name="version"/>, given the
    /// persisted record: counting restarts when a different release fails.
    /// </summary>
    public static int InstallFailureCount(string version, string failedVersion, int previousCount) =>
        (version == failedVersion ? previousCount : 0) + 1;

    /// <summary>
    /// Whether the next update check should retry the install automatically. Past the cap a
    /// failure is treated as permanent: retrying would show a toast and redownload on every
    /// check and wake, forever.
    /// </summary>
    public static bool ShouldRetryAutoInstall(int failures) => failures < MaxAutoInstallAttempts;

    /// <summary>
    /// An adaptive update-check interval, in seconds, from recent release dates: half the average
    /// gap between releases, clamped to [4h, 24h]. Falls back to 12h with fewer than two dates
    /// or no positive gaps.
    /// </summary>
    public static double AdaptiveCheckInterval(IEnumerable<DateTimeOffset> dates)
    {
        const double fallback = 12 * 3600;
        var sorted = dates.OrderByDescending(d => d).ToList();
        if (sorted.Count < 2) return fallback;
        var gaps = new List<double>();
        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var gap = (sorted[i] - sorted[i + 1]).TotalSeconds;
            if (gap > 0) gaps.Add(gap);
        }
        if (gaps.Count == 0) return fallback;
        return Math.Max(4 * 3600, Math.Min(24 * 3600, gaps.Average() * 0.5));
    }
}
