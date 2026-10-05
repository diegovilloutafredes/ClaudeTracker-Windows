using ClaudeTracker.Core;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace ClaudeTracker.Core.Tests;

/// <summary>
/// The update-install tests that apply on Windows: the port of the signature and retry-cap
/// tests of AtomicInstallTests in the macOS suite's PureLogicTests.swift, one test per Swift
/// test of the same name.
///
/// Its seven filesystem tests (AtomicReplace…, BundleShortVersion…, InstallDestination…) have
/// no twin here: they cover swapping the Mac app bundle in place, and the Windows app runs a
/// signed installer instead (DIVERGENCES 8).
/// </summary>
public class UpdateInstallTests
{
    /// <summary>A fresh Ed25519 key pair — what the Swift tests get from CryptoKit's <c>Curve25519.Signing.PrivateKey()</c>.</summary>
    private sealed class SigningKey
    {
        private readonly byte[] secret = new byte[Ed25519.SecretKeySize];

        public SigningKey()
        {
            Ed25519.GeneratePrivateKey(new SecureRandom(), secret);
            Ed25519.GeneratePublicKey(secret, 0, PublicKey, 0);
        }

        /// <summary>The raw 32-byte public key.</summary>
        public byte[] PublicKey { get; } = new byte[Ed25519.PublicKeySize];

        public byte[] Signature(byte[] message)
        {
            var signature = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(secret, 0, message, 0, message.Length, signature, 0);
            return signature;
        }
    }

    // MARK: - Update signature

    [Fact]
    public void UpdateSignatureAcceptsTheSignedBytes()
    {
        var key = new SigningKey();
        var zip = "zip bytes"u8.ToArray();
        var signature = key.Signature(zip);
        Assert.True(Updates.VerifyUpdateSignature(zip, signature, key.PublicKey));
    }

    [Fact]
    public void UpdateSignatureRejectsTamperedBytesAndOtherKeys()
    {
        var key = new SigningKey();
        var signature = key.Signature("zip bytes"u8.ToArray());
        Assert.False(Updates.VerifyUpdateSignature("zip bytez"u8, signature, key.PublicKey));
        Assert.False(Updates.VerifyUpdateSignature("zip bytes"u8, signature, new SigningKey().PublicKey));
    }

    [Fact]
    public void UpdateSignatureRejectsMalformedInput()
    {
        Assert.False(Updates.VerifyUpdateSignature("zip"u8, signature: [], publicKey: new byte[32]));
        Assert.False(Updates.VerifyUpdateSignature("zip"u8, signature: new byte[64], publicKey: new byte[3]));
    }

    /// <summary>
    /// The embedded public key must be the Keychain key's: <c>Fixtures/signing-check.txt.sig</c>
    /// was made with the Mac repo's <c>scripts/update-signing.swift sign</c>. A mismatch would
    /// ship a build that refuses every future update.
    /// </summary>
    [Fact]
    public void EmbeddedPublicKeyMatchesTheReleaseSigningKey()
    {
        var text = Fixture.Bytes("signing-check.txt");
        var signature = Fixture.Bytes("signing-check.txt.sig");
        Assert.True(Updates.VerifyUpdateSignature(text, signature, Updates.SigningPublicKey));
    }

    /// <summary>
    /// After a release's signature fails, Install would fail the same way: offer only the
    /// release page (Download) for that version, keep other versions installable.
    /// </summary>
    [Fact]
    public void ARejectedSignatureLeavesOnlyTheReleasePage()
    {
        var page = new Uri("https://github.com/x/y/releases/tag/v9.0.0");
        var update = new UpdateInfo(Version: "9.0.0", ReleaseUrl: page,
                                    DownloadUrl: new Uri("https://example.com/ClaudeTracker.zip"),
                                    SignatureUrl: new Uri("https://example.com/ClaudeTracker.zip.sig"));
        var rejected = Updates.InstallableUpdate(update, signatureRejectedVersion: "9.0.0");
        Assert.Null(rejected.DownloadUrl);
        Assert.Null(rejected.SignatureUrl);
        Assert.Equal(page, rejected.ReleaseUrl);
        Assert.NotNull(Updates.InstallableUpdate(update, signatureRejectedVersion: "8.0.0").DownloadUrl);
        Assert.NotNull(Updates.InstallableUpdate(update, signatureRejectedVersion: null).DownloadUrl);
    }

    // MARK: - Auto-install retry cap

    [Fact]
    public void InstallFailureCountAccumulatesForTheSameVersion()
    {
        Assert.Equal(3, Updates.InstallFailureCount(version: "1.30.0", failedVersion: "1.30.0", previousCount: 2));
    }

    [Fact]
    public void InstallFailureCountRestartsForANewVersion()
    {
        Assert.Equal(1, Updates.InstallFailureCount(version: "1.31.0", failedVersion: "1.30.0", previousCount: 3));
    }

    [Fact]
    public void AutoInstallStopsRetryingAtTheCap()
    {
        Assert.True(Updates.ShouldRetryAutoInstall(failures: Updates.MaxAutoInstallAttempts - 1));
        Assert.False(Updates.ShouldRetryAutoInstall(failures: Updates.MaxAutoInstallAttempts));
    }
}
