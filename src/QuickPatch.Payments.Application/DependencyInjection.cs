using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Application.Payments;

namespace QuickPatch.Payments.Application;

/// <summary>
/// Capa de aplicación: casos de uso, comandos y consultas (SDD 6.3).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ServiceRequestCompletedHandler>();
        services.AddScoped<GetPaymentHandler>();
        services.AddScoped<PayHandler>();
        services.AddScoped<PaymentSettlement>();
        services.AddScoped<ReconcilePaymentsHandler>();
        services.AddScoped<GetInvoiceHandler>();
        services.AddScoped<ReceivedPaymentsHandler>();
        return services;
    }
}