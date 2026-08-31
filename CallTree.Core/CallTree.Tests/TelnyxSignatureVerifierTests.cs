using System.Text;
using CallTree.Messaging.Configuration;
using CallTree.Messaging.Telnyx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// The webhook's door. This is the only check in front of the one endpoint that is meant to be
/// reachable from the public internet, and reaching it is enough to make the instance send a text at
/// the operator's expense — so "does a wrong signature actually get turned away" is worth asserting
/// rather than assuming.
/// </summary>
public class TelnyxSignatureVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    // A fixed seed rather than a generated key, so a failure is reproducible.
    private static readonly Ed25519PrivateKeyParameters PrivateKey =
        new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 0);

    private static readonly string PublicKeyBase64 =
        Convert.ToBase64String(PrivateKey.GeneratePublicKey().GetEncoded());

    private const string Body = """{"data":{"event_type":"message.received"}}""";

    private static TelnyxSignatureVerifier Verifier(MessagingOptions options) =>
        new(new StaticMonitor<MessagingOptions>(options), NullLogger<TelnyxSignatureVerifier>.Instance);

    private static MessagingOptions Configured() => new()
    {
        Enabled = true,
        PublicKey = PublicKeyBase64,
        RequireSignature = true,
        SignatureToleranceSeconds = 300,
    };

    /// <summary>Signs exactly what the provider signs: the timestamp, a pipe, and the raw body.</summary>
    private static string Sign(string timestamp, string body)
    {
        var data = Encoding.UTF8.GetBytes($"{timestamp}|{body}");
        var signer = new Ed25519Signer();
        signer.Init(true, PrivateKey);
        signer.BlockUpdate(data, 0, data.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private static string Timestamp(DateTimeOffset when) => when.ToUnixTimeSeconds().ToString();

    [Fact]
    public void Accepts_a_genuine_request()
    {
        var timestamp = Timestamp(Now);

        var accepted = Verifier(Configured())
            .Verify(Body, Sign(timestamp, Body), timestamp, Now, out var failure);

        Assert.True(accepted, failure);
        Assert.Equal("", failure);
    }

    [Fact]
    public void Refuses_a_body_that_was_changed_after_signing()
    {
        var timestamp = Timestamp(Now);
        var signature = Sign(timestamp, Body);

        var accepted = Verifier(Configured())
            .Verify(Body.Replace("received", "sent"), signature, timestamp, Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("does not match", failure);
    }

    [Fact]
    public void Refuses_a_signature_made_for_a_different_timestamp()
    {
        // The timestamp is inside the signed message, so replaying a captured request under a fresh
        // timestamp has to fail here rather than only on the age check.
        var signature = Sign(Timestamp(Now.AddMinutes(-1)), Body);

        var accepted = Verifier(Configured())
            .Verify(Body, signature, Timestamp(Now), Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("does not match", failure);
    }

    [Fact]
    public void Refuses_a_request_that_is_too_old_to_be_live()
    {
        var replayed = Now.AddMinutes(-10);
        var timestamp = Timestamp(replayed);

        var accepted = Verifier(Configured())
            .Verify(Body, Sign(timestamp, Body), timestamp, Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("out of date", failure);
    }

    [Fact]
    public void Refuses_a_request_dated_too_far_in_the_future()
    {
        var timestamp = Timestamp(Now.AddMinutes(10));

        var accepted = Verifier(Configured())
            .Verify(Body, Sign(timestamp, Body), timestamp, Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("out of date", failure);
    }

    [Theory]
    [InlineData(null, "12345")]
    [InlineData("c2ln", null)]
    public void Refuses_a_request_missing_either_header(string? signature, string? timestamp)
    {
        var accepted = Verifier(Configured()).Verify(Body, signature, timestamp, Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("missing", failure);
    }

    [Fact]
    public void Refuses_a_signature_that_is_not_base64()
    {
        var accepted = Verifier(Configured())
            .Verify(Body, "not base64 at all!", Timestamp(Now), Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("base64", failure);
    }

    [Fact]
    public void Refuses_a_signature_of_the_wrong_length()
    {
        var accepted = Verifier(Configured())
            .Verify(Body, Convert.ToBase64String(new byte[32]), Timestamp(Now), Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("64", failure);
    }

    [Fact]
    public void Refuses_everything_when_no_public_key_is_configured()
    {
        // Fails closed on purpose. Accepting unsigned traffic because the key has not been pasted in yet
        // would leave the door open for exactly as long as the setup is unfinished.
        var timestamp = Timestamp(Now);

        var accepted = Verifier(Configured() with { PublicKey = "" })
            .Verify(Body, Sign(timestamp, Body), timestamp, Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("PublicKey", failure);
    }

    [Fact]
    public void Refuses_a_public_key_that_is_not_an_ed25519_key()
    {
        var timestamp = Timestamp(Now);

        var accepted = Verifier(Configured() with { PublicKey = Convert.ToBase64String(new byte[16]) })
            .Verify(Body, Sign(timestamp, Body), timestamp, Now, out var failure);

        Assert.False(accepted);
        Assert.Contains("32-byte", failure);
    }

    [Fact]
    public void Accepts_anything_only_when_the_check_has_been_deliberately_switched_off()
    {
        var accepted = Verifier(Configured() with { RequireSignature = false })
            .Verify(Body, signature: null, timestamp: null, Now, out var failure);

        Assert.True(accepted);
        Assert.Equal("", failure);
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
