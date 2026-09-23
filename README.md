# payments

**Tecnología:** ASP.NET Core

## Responsabilidad

Pagos, PSP, tokenización permitida y facturación.

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.
