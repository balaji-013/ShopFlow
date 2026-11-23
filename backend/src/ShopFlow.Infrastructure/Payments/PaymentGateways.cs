using System.Net.Http.Json;
using ShopFlow.Application.Abstractions;

namespace ShopFlow.Infrastructure.Payments;

/// <summary>Used in dev/tests when no provider is configured. Always succeeds instantly.</summary>
public class FakePaymentGateway : IPaymentGateway
{
    public Task<PaymentIntent> CreateIntentAsync(Guid orderId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new PaymentIntent($"pi_fake_{Guid.NewGuid():N}", "fake"));
}

/// <summary>
/// Real provider client. It is registered with AddStandardResilienceHandler() (see DependencyInjection):
/// total timeout + per-attempt timeout + exponential retry with jitter + circuit breaker + rate limiter.
/// "The payment provider is down. What does the user see?" -> after the breaker opens, calls fail FAST (no 30 s hangs) and the
/// controller returns 503 with a retry hint while the order stays Pending and can be paid later.
/// </summary>
public class HttpPaymentGateway(HttpClient http) : IPaymentGateway
{
    private record IntentResponse(string Id);

    public async Task<PaymentIntent> CreateIntentAsync(Guid orderId, decimal amount, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/intents") { Content = JsonContent.Create(new { orderId, amount, currency = "usd" }) };
        req.Headers.Add("Idempotency-Key", orderId.ToString());          // retries must not create two charges
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<IntentResponse>(ct) ?? throw new InvalidOperationException("Empty provider response.");
        return new PaymentIntent(body.Id, "http");
    }
}
