using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using ShopFlow.Application.Abstractions;

namespace ShopFlow.Api.Infrastructure;

/// <summary>Reads identity from the VALIDATED token on the current request. Outside a request (background worker) it is "nobody".</summary>
public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid UserId => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : Guid.Empty;
    public Guid TenantId => Guid.TryParse(Principal?.FindFirstValue("tenant_id"), out var id) ? id : Guid.Empty;
    public bool IsAdmin => Principal?.IsInRole("Admin") == true;
}

/// <summary>SignalR addresses users by "sub" (we disabled the legacy claim-type mapping, so NameIdentifier is empty).</summary>
public class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirstValue("sub");
}
