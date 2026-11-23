using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Auth;

namespace ShopFlow.Api.Controllers;

public record SessionResponse(string AccessToken, int ExpiresIn, UserDto User);

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}/auth"), EnableRateLimiting("auth")]
public class AuthController(AuthService auth, ICurrentUser me, IWebHostEnvironment env) : ControllerBase
{
    private const string RefreshCookie = "sf_refresh";

    [HttpPost("register"), AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Register(RegisterRequest req, CancellationToken ct) => Session(await auth.RegisterAsync(req, ct));

    [HttpPost("login"), AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Login(LoginRequest req, CancellationToken ct) => Session(await auth.LoginAsync(req, ct));

    /// <summary>The refresh token is read from an httpOnly cookie, so JavaScript (and therefore XSS) can never read it.</summary>
    [HttpPost("refresh"), AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Refresh(CancellationToken ct)
    {
        try { return Session(await auth.RefreshAsync(Request.Cookies[RefreshCookie], ct)); }
        catch { Response.Cookies.Delete(RefreshCookie, CookieOptions(null)); throw; }
    }

    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(null));
        return NoContent();
    }

    [HttpGet("me"), Authorize, DisableRateLimiting]
    public ActionResult<object> Me() => new { id = me.UserId, tenantId = me.TenantId, isAdmin = me.IsAdmin };

    private ActionResult<SessionResponse> Session(AuthResult r)
    {
        Response.Cookies.Append(RefreshCookie, r.RefreshToken, CookieOptions(DateTimeOffset.UtcNow.AddDays(7)));
        return new SessionResponse(r.AccessToken, r.ExpiresIn, r.User);   // refresh token is NOT in the body
    }

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,                    // invisible to JS => XSS cannot steal it
        Secure = !env.IsDevelopment(),      // HTTPS only outside local dev
        SameSite = SameSiteMode.Strict,     // never sent on cross-site requests => CSRF cannot trigger /refresh
        Path = "/api/v1/auth",              // not sent to any other endpoint => smaller exposure
        Expires = expires,
    };
}
