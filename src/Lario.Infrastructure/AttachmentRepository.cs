using Lario.Application.Attachments;
using Lario.Domain.Attachments;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

public sealed class AttachmentRepository(LarioDbContext db) : IAttachmentRepository
{
    public async Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Attachment?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
        => db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Attachment>> GetByBookingAsync(string bookingId, CancellationToken cancellationToken = default)
        => await db.Attachments.AsNoTracking()
            .Where(a => a.BookingId == bookingId)
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Attachment>> GetAllAsync(CancellationToken cancellationToken = default)
        => await db.Attachments.AsNoTracking()
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null)
            return;
        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateSplitReferenceAsync(
        string id, string? splitId, string? originalSplitId, CancellationToken cancellationToken = default)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Beleg '{id}' existiert nicht.");
        attachment.SplitId = splitId;
        attachment.OriginalSplitId = originalSplitId;
        await db.SaveChangesAsync(cancellationToken);
    }
}
