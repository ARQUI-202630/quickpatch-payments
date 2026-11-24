using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>Idempotencia de los consumidores (RN-EV1) con la implementación común de ADR-020.</summary>
public sealed class ProcessedEventRepository(PaymentsDbContext db) : ProcessedEventStore<PaymentsDbContext>(db), IProcessedEventRepository;