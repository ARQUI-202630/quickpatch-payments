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
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_scheduler') THEN
        CREATE ROLE payments_scheduler NOLOGIN BYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO payments_app, payments_outbox, payments_scheduler;

-- Rol del servicio: todo bajo RLS. Los pagos no se borran; los comprobantes no se modifican.
GRANT SELECT, INSERT, UPDATE ON payments TO payments_app;
GRANT SELECT, INSERT ON invoices TO payments_app;
GRANT SELECT, INSERT ON processed_events TO payments_app;
GRANT INSERT ON outbox_events TO payments_app;

-- Rol del publicador del Outbox: solo lee y marca eventos; lo adopta el servicio con SET LOCAL ROLE.
GRANT SELECT, UPDATE ON outbox_events TO payments_outbox;
GRANT payments_outbox TO payments_app WITH INHERIT FALSE, SET TRUE;

-- Rol de las tareas programadas (DD 10.2, <servicio>_scheduler): la conciliación de pagos (AC9-E5) recorre los pagos
-- de todos los tenants para encontrar los que quedaron procesando. Solo lee las columnas que necesita; cada pago se
-- cierra después bajo su tenant, con payments_app y RLS.
GRANT SELECT (id, tenant_id, status, provider_token_ref, processing_started_at) ON payments TO payments_scheduler;
GRANT payments_scheduler TO payments_app WITH INHERIT FALSE, SET TRUE;
