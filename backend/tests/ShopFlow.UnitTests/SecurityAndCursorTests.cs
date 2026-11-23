using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Application.Common;
using ShopFlow.Infrastructure.Security;

namespace ShopFlow.UnitTests;

public class CursorTests
{
    [Fact]
    public void Round_trips()
    {
        var at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc); var id = Guid.NewGuid();
        Cursor.TryDecode(Cursor.Encode(at, id), out var a2, out var id2).Should().BeTrue();
        (a2, id2).Should().Be((at, id));
    }

    [Theory, InlineData(null), InlineData(""), InlineData("not-base64!"), InlineData("e30=")]
    public void Garbage_is_rejected_not_thrown(string? s) => Cursor.TryDecode(s, out _, out _).Should().BeFalse();
}

public class WebhookSignatureTests
{
    private static (HmacWebhookSignatureVerifier V, FakeTimeProvider Clock) Make(string secret = "s3cret")
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        return (new HmacWebhookSignatureVerifier(Options.Create(new WebhookOptions { Secret = secret }), clock), clock);
    }

    [Fact]
    public void Valid_signature_passes()
    {
        var (v, clock) = Make();
        v.IsValid("{\"a\":1}", v.Sign("{\"a\":1}", clock.GetUtcNow())).Should().BeTrue();
    }

    [Fact]
    public void Tampered_body_fails()
    {
        var (v, clock) = Make();
        v.IsValid("{\"a\":2}", v.Sign("{\"a\":1}", clock.GetUtcNow())).Should().BeFalse();
    }

    [Fact]
    public void Signature_from_another_secret_fails()
    {
        var (v, clock) = Make(); var (other, _) = Make("different");
        v.IsValid("body", other.Sign("body", clock.GetUtcNow())).Should().BeFalse();
    }

    [Fact]
    public void Replay_of_an_old_capture_is_rejected_by_timestamp_tolerance()
    {
        var (v, clock) = Make();
        var sig = v.Sign("body", clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(6));          // FakeTimeProvider: time travel without sleeping
        v.IsValid("body", sig).Should().BeFalse();
    }

    [Theory, InlineData(""), InlineData("garbage"), InlineData("t=abc,v1=00"), InlineData("t=1")]
    public void Malformed_header_fails_safely(string header) => Make().V.IsValid("body", header).Should().BeFalse();
}

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _h = new();

    [Fact]
    public void Same_password_hashes_differently_because_of_random_salt() => _h.Hash("hunter2hunter2").Should().NotBe(_h.Hash("hunter2hunter2"));

    [Fact]
    public void Verifies_correct_and_rejects_wrong()
    {
        var hash = _h.Hash("correct horse battery");
        _h.Verify(hash, "correct horse battery", out _).Should().BeTrue();
        _h.Verify(hash, "wrong", out _).Should().BeFalse();
        hash.Should().NotContain("correct");
    }
}
