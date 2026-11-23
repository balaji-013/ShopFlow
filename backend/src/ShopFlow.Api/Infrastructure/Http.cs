using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopFlow.Application.Common;
using ShopFlow.Domain.Common;

namespace ShopFlow.Api.Infrastructure;

/// <summary>
/// ONE place that turns exceptions into HTTP. Controllers stay free of try/catch, and the error contract is uniform:
/// RFC 9457 "application/problem+json" with a stable machine-readable `code` the Angular client can switch on.
///   400 malformed input (validation)   401 not authenticated   403 authenticated but not allowed
///   404 not found (also used to hide existence)   409 conflict with current state (stock, concurrency, duplicates)
///   422 well-formed but violates a business rule   503 dependency down   500 our bug (details NEVER leaked to the client)
/// </summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, code, title, errors) = ex switch
        {
            ValidationException v => (400, "validation_failed", "One or more validation errors occurred.",
                v.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())),
            NotFoundException n => (404, n.Code, n.Message, null),
            UnauthorizedAppException u => (401, u.Code, u.Message, null),
            ForbiddenAppException f => (403, f.Code, f.Message, null),
            ConflictException c => (409, c.Code, c.Message, null),
            UnprocessableException p => (422, p.Code, p.Message, null),
            DomainException d => (422, d.Code, d.Message, null),
            DbUpdateConcurrencyException => (409, "concurrency_conflict", "The resource was modified by someone else.", null),
            HttpRequestException => (503, "dependency_unavailable", "A dependency is temporarily unavailable. Please retry shortly.", null),
            Polly.CircuitBreaker.BrokenCircuitException => (503, "dependency_unavailable", "A dependency is temporarily unavailable. Please retry shortly.", null),
            OperationCanceledException when ctx.RequestAborted.IsCancellationRequested => (499, "client_closed_request", "Request cancelled.", null),
            _ => (500, "internal_error", "An unexpected error occurred.", null),
        };

        if (status >= 500) log.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
        else log.LogInformation("Handled {Code} on {Path}: {Message}", code, ctx.Request.Path, ex.Message);

        if (status == 503) ctx.Response.Headers.RetryAfter = "5";
        ctx.Response.StatusCode = status;

        var pd = new ProblemDetails { Status = status, Title = title };
        pd.Extensions["code"] = code;
        if (errors is not null) pd.Extensions["errors"] = errors;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, ProblemDetails = pd, Exception = ex });
    }
}

public class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";
    public async Task Invoke(HttpContext ctx, ILogger<CorrelationIdMiddleware> log)
    {
        var id = ctx.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) id = Guid.NewGuid().ToString("N");
        ctx.Response.Headers[Header] = id;
        using (log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))   // every log line of this request carries it
            await next(ctx);
    }
}

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";   // an API serves JSON only; it should never run script
        h["Cache-Control"] = "no-store";                                               // authenticated JSON must not sit in shared caches
        return next(ctx);
    }
}
