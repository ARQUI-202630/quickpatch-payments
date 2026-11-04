# payments

**Tecnología:** ASP.NET Core

## Responsabilidad

Pagos, pasarela (PSP), tokenización permitida y facturación.

## Qué hace hoy (SCRUM-42, SCRUM-44 y SCRUM-501)

|Pieza|Detalle|
|---|---|
|Contrato REST|`payments.v1.yaml` 1.2.0 (submódulo `contracts/api-gateway` v0.19.0): `GET` y `POST /v1/service-requests/{id}/payment`, `GET /v1/payments/{id}/invoice` y `GET /v1/payments/received`. En k3s las rutas llegan por un `IngressRoute` de Traefik (`deploy/k8s/payments.yaml`).|
|Eventos|Consume `service-request.completed` y crea el pago `pendiente` con el total de la cotización aceptada (RN-P3, RN-P4). Publica `payment.approved` y `payment.rejected` por Outbox (ADR-007), con la solicitud como clave (`contracts/kafka` v0.9.0).|
|Pasarela|**Simulada** (ADR-009, modificación del 20 de octubre de 2026) detrás del puerto `IPaymentProvider`. La app cambia la tarjeta por un token en el dispositivo; aquí solo llega el token (RN-P1). `tok_sim_aprobada_…` aprueba, `tok_sim_rechazada_…` rechaza por `fondos_insuficientes` y `tok_sim_error_…` no responde (`502`).|
|Un solo cobro|El pago se bloquea (`FOR UPDATE`) y solo una petición lo pasa a `procesando`; el índice único `ux_payments_open` impide dos pagos abiertos (RN-P5) y `ux_payments_approved` dos aprobados (RN-P2). El cobro se hace fuera de la transacción.|
|Conciliación|`ReconciliationJob` cada 30 s cierra los pagos que quedaron `procesando` sin respuesta (AC9-E5). Lee todos los tenants con el rol `payments_scheduler` y cierra cada pago bajo su tenant.|
|Comprobante|Se emite en la misma transacción que aprueba el pago (`invoices`, número `QP-AAAAMMDD-…`). `GET /v1/payments/{id}/invoice` lo entrega en JSON o, con `Accept: application/pdf`, en un PDF que se genera al pedirlo (`InvoicePdfRenderer`, sin librerías ni fuentes externas). Lo ven el pagador y el `admin_tenant`. Es un comprobante **sin validez fiscal**, rotulado como tal (SRS P3, opción a).|
|Quién paga|El usuario que creó la solicitud (`clientId`). El pago por cualquier contacto de la empresa (RN-P6) llega con SCRUM-43, cuando el token traiga la empresa.|
|Pagos recibidos|`GET /v1/payments/received` (RF-25, SCRUM-501): el `tecnico` ve, bajo RLS, los pagos de sus servicios (`pendiente`, `procesando` y `aprobado`; los rechazados no), del más reciente al más antiguo y paginados, sin datos de tarjeta (RN-P1). Usa el índice `ix_payments_technician_status`. La vista del `proveedor` (pagos de su equipo, P-02) está pendiente.|

Base `db_payments` con RLS en `payments`, `invoices`, `processed_events` y `outbox_events`; roles en `db/roles.sql` (DD 10.2).

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.

## Estructura

Cuatro capas, según el SDD (secciones 6.3 y 6.5):

```text
QuickPatch.Payments.slnx
src/
  QuickPatch.Payments.Api/              Endpoints, validación de entrada y raíz de composición
  QuickPatch.Payments.Application/      Casos de uso, comandos y consultas
  QuickPatch.Payments.Domain/           Entidades, value objects y reglas de negocio (sin dependencias externas)
  QuickPatch.Payments.Infrastructure/   PostgreSQL, Kafka y la pasarela de pagos
tests/
  unit/QuickPatch.Payments.UnitTests/
  integration/QuickPatch.Payments.IntegrationTests/
```

Dependencias permitidas: `Api → Application → Domain`; `Infrastructure → Application, Domain`. `Api` referencia `Infrastructure` solo para registrar sus servicios.

## Desarrollo local

Requiere el SDK de .NET indicado en `global.json`.

```bash
dotnet restore
dotnet format --verify-no-changes   # lint, igual que el CI
dotnet build -c Release
dotnet test tests/unit/QuickPatch.Payments.UnitTests
dotnet test tests/integration/QuickPatch.Payments.IntegrationTests
dotnet run --project src/QuickPatch.Payments.Api
```

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).
