using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Payments.Infrastructure.Persistence;

public sealed class EfOutbox(PaymentsDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxEvents.Add(OutboxEventRecord.Pending(message.Id, message.TenantId, message.AggregateId, message.EventType, message.Payload, clock.GetUtcNow()));
    }
}