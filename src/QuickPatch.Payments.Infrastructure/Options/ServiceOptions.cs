namespace QuickPatch.Payments.Infrastructure.Options;

/// <summary>Conexión a Kafka (VM6). Sección <c>Kafka</c>; sin servidores, el Outbox y el consumidor no arrancan.</summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string ConsumerGroupId { get; set; } = "payments-service";

    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Publicador del Outbox (ADR-007). Sección <c>Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>
    /// Rol <c>BYPASSRLS</c> que adopta el publicador con <c>SET LOCAL ROLE</c> para leer los eventos de todos
    /// los tenants (DD, sección 10.2). Vacío solo en desarrollo local.
    /// </summary>
    public string PublisherRole { get; set; } = string.Empty;

    public int BatchSize { get; set; } = 50;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Conciliación de pagos sin respuesta de la pasarela (AC9-E5). Sección <c>Reconciliation</c>.</summary>
public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    /// <summary>Rol <c>BYPASSRLS</c> de solo lectura sobre <c>payments</c> (DD 10.2). Vacío solo en desarrollo local.</summary>
    public string ReaderRole { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Espera mínima antes de consultar un cobro que la pasarela dejó sin respuesta.</summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Un pago sin referencia de la pasarela tras este tiempo quedó huérfano (el servicio se cayó durante el cobro).</summary>
    public TimeSpan OrphanAge { get; set; } = TimeSpan.FromMinutes(5);

    public int BatchSize { get; set; } = 50;
}