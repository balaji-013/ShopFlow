using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using ShopFlow.Application.Abstractions;
using ShopFlow.Infrastructure.Messaging;
using ShopFlow.Infrastructure.Payments;
using ShopFlow.Infrastructure.Persistence;
using ShopFlow.Infrastructure.Security;

namespace ShopFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, bool runOutboxWorker = true)
    {
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(config.GetConnectionString("Default"),
            sql => sql.EnableRetryOnFailure(maxRetryCount: 5)));   // transient faults (failover, deadlock victim, timeouts) are retried automatically
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<DbSeeder>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<WebhookOptions>(config.GetSection(WebhookOptions.Section));
        services.AddSingleton(TimeProvider.System);          // inject the clock => tests can freeze/advance time
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IWebhookSignatureVerifier, HmacWebhookSignatureVerifier>();

        var paymentsUrl = config["Payments:BaseUrl"];
        if (string.IsNullOrWhiteSpace(paymentsUrl)) services.AddSingleton<IPaymentGateway, FakePaymentGateway>();
        else
        {
            services.AddHttpClient<IPaymentGateway, HttpPaymentGateway>(c => c.BaseAddress = new Uri(paymentsUrl))
                .AddStandardResilienceHandler();
        }

        services.AddScoped<OutboxDispatcher>();
        if (runOutboxWorker) services.AddHostedService<OutboxWorker>();
        return services;
    }
}
