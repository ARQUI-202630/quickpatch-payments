using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;

namespace QuickPatch.Payments.Infrastructure.Persistence;

public sealed class EfOutbox(PaymentsDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxEvents.Add(new OutboxEventRecord
        {
            Id = message.Id,
            TenantId = message.TenantId,
            AggregateId = message.AggregateId,
            EventType = message.EventType,
            Payload = message.Payload,
            CreatedAt = clock.GetUtcNow(),
            Attempts = 0,
        });
    }
}