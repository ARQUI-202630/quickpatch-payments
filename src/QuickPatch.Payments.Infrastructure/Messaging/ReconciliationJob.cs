using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.Infrastructure.Options;

namespace QuickPatch.Payments.Infrastructure.Messaging;

/// <summary>Tarea periódica de conciliación (AC9-E5): cierra los pagos que la pasarela dejó sin respuesta.</summary>
public sealed partial class ReconciliationJob(
    IServiceScopeFactory scopes,
    IOptions<ReconciliationOptions> options,
    TimeProvider clock,
    ILogger<ReconciliationJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }

            await Task.Delay(options.Value.Interval, clock, stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var closed = await scope.ServiceProvider.GetRequiredService<ReconcilePaymentsHandler>().HandleAsync(cancellationToken);
        if (closed > 0)
        {
            LogClosed(logger, closed);
        }

        return closed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "La conciliación cerró {Count} pagos sin respuesta de la pasarela.")]
    private static partial void LogClosed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la conciliación de pagos; se reintentará.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}