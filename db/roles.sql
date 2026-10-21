-- Roles y permisos de Payments Service (DD, sección 10.2).
-- Lo ejecuta un administrador de la base DESPUÉS de las migraciones (que crean las tablas y las políticas RLS).
-- Las contraseñas no van aquí: se asignan aparte con ALTER ROLE ... LOGIN PASSWORD, desde el vault.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_app') THEN
        CREATE ROLE payments_app NOLOGIN NOBYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_outbox') THEN
        CREATE ROLE payments_outbox NOLOGIN BYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_reconciler') THEN
        CREATE ROLE payments_reconciler NOLOGIN BYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO payments_app, payments_outbox, payments_reconciler;

-- Rol del servicio: todo bajo RLS. Los pagos no se borran; los comprobantes no se modifican.
GRANT SELECT, INSERT, UPDATE ON payments TO payments_app;
GRANT SELECT, INSERT ON invoices TO payments_app;
GRANT SELECT, INSERT ON processed_events TO payments_app;
GRANT INSERT ON outbox_events TO payments_app;

-- Rol del publicador del Outbox: solo lee y marca eventos; lo adopta el servicio con SET LOCAL ROLE.
GRANT SELECT, UPDATE ON outbox_events TO payments_outbox;
GRANT payments_outbox TO payments_app WITH INHERIT FALSE, SET TRUE;

-- Rol de la conciliación (AC9-E5): solo lee los pagos de todos los tenants para encontrar los que quedaron
-- procesando; cada pago se cierra después bajo su tenant, con payments_app y RLS.
GRANT SELECT ON payments TO payments_reconciler;
GRANT payments_reconciler TO payments_app WITH INHERIT FALSE, SET TRUE;
