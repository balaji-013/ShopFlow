using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Application.Auth;
using ShopFlow.Application.Catalog;
using ShopFlow.Application.Orders;
using ShopFlow.Application.Payments;
using ShopFlow.Application.Reports;

namespace ShopFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<RegisterValidator>();
        services.AddMemoryCache();
        services.AddSingleton<CatalogCache>();          // singleton: it owns the cache tokens
        services.AddScoped<AuthService>();
        services.AddScoped<ProductService>();
        services.AddScoped<OrderService>();
        services.AddScoped<PaymentWebhookService>();
        services.AddScoped<ReportService>();
        return services;
    }
}

