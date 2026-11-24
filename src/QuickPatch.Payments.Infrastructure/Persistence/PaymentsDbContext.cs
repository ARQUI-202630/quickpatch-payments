using Microsoft.EntityFrameworkCore;

using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>Base de datos de Payments Service (DD, secciones 5.12, 5.13, 5.15 y 5.16).</summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string OpenPaymentIndex = "ux_payments_open";

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<PaymentTechnician> PaymentTechnicians => Set<PaymentTechnician>();

    public DbSet<OutboxEventRecord> OutboxEvents => Set<OutboxEventRecord>();

    public DbSet<ProcessedEventRecord> ProcessedEvents => Set<ProcessedEventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ServiceRequestId).HasColumnName("service_request_id");
            e.Property(x => x.TechnicianId).HasColumnName("technician_id");
            e.Property(x => x.ClientId).HasColumnName("client_id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.PayerType).HasColumnName("payer_type").HasMaxLength(20);
            e.Property(x => x.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
            e.Property(x => x.ProviderTokenRef).HasColumnName("provider_token_ref").HasMaxLength(255);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(50);
            e.Property(x => x.Billing).HasColumnName("billing").HasColumnType("jsonb");
            e.Property(x => x.ProcessingStartedAt).HasColumnName("processing_started_at");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Ignore(x => x.AmountText);
            e.HasIndex(x => x.ServiceRequestId, "ix_payments_service_request");
            e.HasIndex(x => new { x.TechnicianId, x.Status }).HasDatabaseName("ix_payments_technician_status");

            // RN-P2: como máximo un pago aprobado por solicitud. RN-P5: como máximo un pago abierto.
            e.HasIndex(x => x.ServiceRequestId, "ux_payments_approved").IsUnique().HasFilter("status = 'aprobado'");
            e.HasIndex(x => x.ServiceRequestId, OpenPaymentIndex).IsUnique().HasFilter("status IN ('pendiente', 'procesando')");
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InvoiceNumber).HasColumnName("invoice_number").HasMaxLength(50);
            e.Property(x => x.PayerName).HasColumnName("payer_name").HasMaxLength(150);
            e.Property(x => x.PayerNit).HasColumnName("payer_nit").HasMaxLength(30);
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(12, 2);
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.PdfUrl).HasColumnName("pdf_url").HasMaxLength(500);
            e.HasIndex(x => x.PaymentId).IsUnique().HasDatabaseName("ux_invoices_payment");
            e.HasIndex(x => x.InvoiceNumber).IsUnique().HasDatabaseName("ux_invoices_number");
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });

        // Tablas transversales con el mapeo común (ADR-020); las migraciones siguen siendo de este servicio.
        modelBuilder.MapOutboxEvents().MapProcessedEvents();

        modelBuilder.Entity<PaymentTechnician>(e =>
        {
            e.ToTable("payment_technicians");
            e.HasKey(x => x.TechnicianId);
            e.Property(x => x.TechnicianId).HasColumnName("technician_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.ProviderId).HasDatabaseName("ix_payment_technicians_provider");
        });

    }
}