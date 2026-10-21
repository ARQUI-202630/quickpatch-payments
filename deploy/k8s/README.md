# Despliegue de Payments en k3s

`payments.yaml` crea el ConfigMap, el Deployment (con las migraciones como init container), el Service y el `IngressRoute` de Traefik (la ruta `/v1/service-requests/{id}/payment`, DD 8.2.5) en el namespace `quickpatch`. Es igual en QA y en producción; solo cambian los secretos y la dirección de Kafka.

## Secreto `payments-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`migrator-connection`|Cadena de conexión con `payments_migrator` (dueño de las tablas; solo para las migraciones)|
|`app-connection`|Cadena de conexión con `payments_app` (sin `BYPASSRLS`)|
|`jwt-public-key`|Llave pública RSA de Identity en PEM, para validar los tokens (ADR-018)|

```bash
kubectl -n quickpatch create secret generic payments-secretos \
  --from-literal=migrator-connection='Host=<vm-datos>;Port=5432;Database=db_payments;Username=payments_migrator;Password=<...>' \
  --from-literal=app-connection='Host=<vm-datos>;Port=5432;Database=db_payments;Username=payments_app;Password=<...>' \
  --from-file=jwt-public-key=identity-public.pem
```

## Primer despliegue

1. Base: `db_payments` con dueño `payments_migrator` y el rol `payments_app` (Ansible). Topics `payment.approved` y `payment.rejected` creados con quickpatch-kafka v0.9.0.
2. Reemplazar `<vm-mensajeria>` en el ConfigMap por la IP de Kafka del ambiente y `kubectl apply -f deploy/k8s/payments.yaml`. El pod queda esperando la imagen (`:pendiente`).
3. Push a `release/*`: el pipeline publica la imagen y la fija en el Deployment; el init container aplica las migraciones.
4. Solo la primera vez, un administrador de la base ejecuta `db/roles.sql` (crea `payments_app`, `payments_outbox` y `payments_reconciler`) y reinicia el servicio con `kubectl -n quickpatch rollout restart deployment/payments`.
