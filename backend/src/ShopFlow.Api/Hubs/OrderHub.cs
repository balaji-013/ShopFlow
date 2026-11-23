using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ShopFlow.Application.Abstractions;

namespace ShopFlow.Api.Hubs;

/// <summary>
/// Real-time channel. WebSockets (with SSE / long-polling fallback) were chosen over polling because order updates are rare, pushed by the
/// server, and many clients would otherwise poll for nothing. Scaling out: several instances need a SignalR BACKPLANE (Redis / Azure SignalR),
/// otherwise a message produced on instance A never reaches a user connected to instance B.
/// </summary>
[Authorize]
public class OrderHub : Hub { }

public class SignalRNotifier(IHubContext<OrderHub> hub) : IRealtimeNotifier
{
    public Task NotifyUserAsync(Guid userId, string eventName, object payload, CancellationToken ct) =>
        hub.Clients.User(userId.ToString()).SendAsync(eventName, payload, ct);
}
