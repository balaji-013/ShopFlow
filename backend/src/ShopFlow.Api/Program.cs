using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using ShopFlow.Api.Hubs;
using ShopFlow.Api.Infrastructure;
using ShopFlow.Application;
using ShopFlow.Application.Abstractions;
using ShopFlow.Infrastructure;
using ShopFlow.Infrastructure.Persistence;
using ShopFlow.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: structured (Serilog) so logs are queryable fields, not strings to grep ──
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}"));

var config = builder.Configuration;

// ── Fail fast on bad config: a missing secret should stop the deploy, not surface at 3 a.m. ──
var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.SigningKey.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters (set it via user-secrets or an environment variable).");
if (string.IsNullOrWhiteSpace(config[$"{WebhookOptions.Section}:Secret"])) throw new InvalidOperationException("Webhooks:Secret is required.");

// ── Services ──
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(config, runOutboxWorker: config.GetValue("Outbox:Enabled", true));
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SubUserIdProvider>();
builder.Services.AddSignalR();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.AssumeDefaultVersionWhenUnspecified = false;
    o.ReportApiVersions = true;            // adds api-supported-versions header => clients can discover deprecations
}).AddMvc();

// ── AuthN: JWT bearer. MapInboundClaims=false keeps claim names as issued ("sub", "role") instead of the legacy long URIs ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub", RoleClaimType = "role",
    };
    // Browsers cannot set an Authorization header on a WebSocket, so SignalR sends the token in the query string - only for the hub path.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = c =>
        {
            var token = c.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && c.HttpContext.Request.Path.StartsWithSegments("/hubs")) c.Token = token;
            return Task.CompletedTask;
        }
    };
});
// ── AuthZ: role-based policy. Row-level ownership is enforced in the data layer (see OrderService.Visible) ──
builder.Services.AddAuthorizationBuilder().AddPolicy("Admin", p => p.RequireRole("Admin"));

// ── CORS: explicit allow-list (never "*" with credentials) ──
var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
    .WithExposedHeaders("X-Correlation-Id", "Idempotent-Replayed")));

// ── Rate limiting: protects cost + availability, and blunts credential stuffing on /auth ──
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)) ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
        return ValueTask.CompletedTask;
    };
    var authPerMin = config.GetValue("RateLimiting:AuthPerMinute", 10);
    var globalPerMin = config.GetValue("RateLimiting:GlobalPerMinute", 300);
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMin, Window = TimeSpan.FromMinutes(1) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetTokenBucketLimiter(http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new TokenBucketRateLimiterOptions { TokenLimit = globalPerMin, TokensPerPeriod = globalPerMin / 6, ReplenishmentPeriod = TimeSpan.FromSeconds(10) }));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("sql", tags: ["ready"]);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // By default only loopback proxies are trusted (anyone could otherwise spoof X-Forwarded-For to dodge the rate limiter).
    // In a container network the proxy is another host, so deployments that KNOW the api is unreachable except through the proxy opt in.
    if (config.GetValue("ForwardedHeaders:TrustAll", false)) { o.KnownNetworks.Clear(); o.KnownProxies.Clear(); }
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "ShopFlow API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});

var app = builder.Build();

// ── Pipeline: ORDER MATTERS (each middleware wraps the next) ──
app.UseForwardedHeaders();                 // 1st: so everything after sees the real client IP / scheme behind a load balancer
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();            // OUTSIDE the exception handler so it logs the FINAL status (404), not a transient 500
app.UseExceptionHandler();                 // catches exceptions from everything below and writes problem+json
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment() && config.GetValue("Security:RequireHttps", true)) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseCors();                             // before auth so preflight OPTIONS requests are answered without a token
app.UseAuthentication();
app.UseRateLimiter();                      // after auth: the global limiter partitions by user id
app.UseAuthorization();

if (app.Environment.IsDevelopment() || config.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHub<OrderHub>("/hubs/orders");
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });                                  // liveness: process is up
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });              // readiness: DB reachable -> LB routes traffic

// Dev convenience: apply migrations + seed. In production migrations run as a separate, controlled deploy step.
if (config.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (config.GetValue("Database:Seed", false)) await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
}

app.Run();

public partial class Program;   // makes the entry point visible to WebApplicationFactory in integration tests

