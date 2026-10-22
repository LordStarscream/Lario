using Lario.Application.Attachments;
using Lario.Domain;
using Lario.Domain.Attachments;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

public sealed class AttachmentRepository(LarioDbContext db) : IAttachmentRepository
{
    /// <summary>
    /// Fügt eine Metadatenzeile hinzu und committet sie (atomar mit dem
    /// Dateispeicher-Aufruf aus dem Service).
    /// </summary>
    /// <remarks>
    /// Review 7a, Befund 3 (Race A): Der Service prüft Buchung/Teilposten
    /// vor dem Dateispeicher; zwischen der Prüfung und diesem Insert kann
    /// eine parallele Operation (z. B. eine Split-entfernende Korrektur)
    /// die Referenz gelöscht und committet haben. Der Komposit-FK
    /// (BookingId, SplitId → Bookings, ExpenseSplits) schlägt dann bei
    /// genau diesem einen Insert fehl — das ist eine ungültige Referenz,
    /// kein interner Fehler: fachliche Ausnahme (400 invalid_reference),
    /// der Service räumt die Datei auf. Alle anderen Datenbankfehler
    /// laufen unverändert weiter (<see cref="SqliteConstraintErrors"/>
    /// filtert strikt auf den erweiterten FK-Fehlercode 787).
    /// </remarks>
    public async Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqliteConstraintErrors.IsForeignKeyViolation(ex))
        {
            var sqlite = ex.GetBaseException() is SqliteException s ? s.Message : ex.Message;
            throw new InvalidReferenceException(
                $"Beleg für Buchung '{attachment.BookingId}': " +
                ReferenceDescription(attachment) +
                " existiert nicht mehr — eine parallele Änderung wurde " +
                $"zwischenzeitlich übernommen (SQLite: {sqlite}).");
        }
    }

    private static string ReferenceDescription(Attachment attachment)
        => attachment.SplitId is null
            ? "die referenzierte Buchung"
            : $"die referenzierte Buchung oder der Teilposten '{attachment.SplitId}'";

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

    /// <summary>
    /// Manuelle Umzuordnung (Nachtrag N1): Teilposten-Zuordnung und
    /// Änderungszeitstempel; <c>OriginalSplitId</c> bleibt unberührt.
    /// Wie bei <see cref="AddAsync"/> ist die Referenz zwischen der
    /// fachlichen Prüfung im Service und diesem Commit parallel wegfallen
    /// können (Korrektur entfernt den Ziel-Split) — der Komposit-FK
    /// schlägt dann fehl und wird als ungültige Referenz (400) gemeldet,
    /// nicht als interner Fehler.
    /// </summary>
    public async Task UpdateSplitAssignmentAsync(
        string id, string? splitId, DateTime updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Beleg '{id}' existiert nicht.");
        attachment.SplitId = splitId;
        attachment.UpdatedAtUtc = updatedAtUtc;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqliteConstraintErrors.IsForeignKeyViolation(ex))
        {
            var sqlite = ex.GetBaseException() is SqliteException s ? s.Message : ex.Message;
            throw new InvalidReferenceException(
                $"Teilposten '{splitId}' der Buchung '{attachment.BookingId}' existiert " +
                $"nicht mehr — eine parallele Änderung wurde zwischenzeitlich übernommen " +
                $"(SQLite: {sqlite}).");
        }
    }
}
