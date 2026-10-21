using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payer_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    provider_token_ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    billing = table.Column<string>(type: "jsonb", nullable: true),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payer_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    payer_nit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pdf_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoices_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_number",
                table: "invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoices_payment",
                table: "invoices",
                column: "payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_pending",
                table: "outbox_events",
                column: "created_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_service_request",
                table: "payments",
                column: "service_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_technician_status",
                table: "payments",
                columns: new[] { "technician_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_payments_approved",
                table: "payments",
                column: "service_request_id",
                unique: true,
                filter: "status = 'aprobado'");

            migrationBuilder.CreateIndex(
                name: "ux_payments_open",
                table: "payments",
                column: "service_request_id",
                unique: true,
                filter: "status IN ('pendiente', 'procesando')");

            // Aislamiento por tenant con RLS (DD 10.2). La conciliación lee todos los tenants con un rol BYPASSRLS propio.
            migrationBuilder.Sql(SqlAdicional);
        }

        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string SqlAdicional = $"""
            ALTER TABLE payments ENABLE ROW LEVEL SECURITY;
            ALTER TABLE payments FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON payments USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});

            ALTER TABLE invoices ENABLE ROW LEVEL SECURITY;
            ALTER TABLE invoices FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON invoices USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});

            ALTER TABLE processed_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE processed_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON processed_events USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});

            -- outbox_events: el servicio solo inserta con el tenant de la sesión; lee y marca como publicado
            -- únicamente el rol del publicador, que tiene BYPASSRLS.
            ALTER TABLE outbox_events ALTER COLUMN tenant_id SET DEFAULT {CurrentTenant};
            ALTER TABLE outbox_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE outbox_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY outbox_insert ON outbox_events FOR INSERT WITH CHECK (tenant_id = {CurrentTenant});
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "outbox_events");

            migrationBuilder.DropTable(
                name: "processed_events");

            migrationBuilder.DropTable(
                name: "payments");
        }
    }
}
