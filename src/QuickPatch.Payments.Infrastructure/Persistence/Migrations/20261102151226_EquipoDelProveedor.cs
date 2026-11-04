using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipoDelProveedor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_technicians",
                columns: table => new
                {
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_technicians", x => x.technician_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_technicians_provider",
                table: "payment_technicians",
                column: "provider_id");

            migrationBuilder.Sql(TenantIsolationSql);
        }

        // Equipo de cada proveedor (SCRUM-501, P-02): mismo aislamiento por tenant que las demás tablas (DD 10.2).
        private const string CurrentTenant = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE payment_technicians ENABLE ROW LEVEL SECURITY;
            ALTER TABLE payment_technicians FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON payment_technicians USING (tenant_id = {CurrentTenant}) WITH CHECK (tenant_id = {CurrentTenant});

            -- El consumidor escribe en esta tabla desde este despliegue; sin el permiso fallaría hasta que se vuelva a aplicar
            -- db/roles.sql. El dueño de la tabla (el migrador) concede el permiso si el rol ya existe.
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON payment_technicians TO payments_app;
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_technicians");
        }
    }
}
