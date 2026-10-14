using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Kontostandskorrektur-Vorgabe (Rechenvertrag §3.3/§4, Design Schritt 6
/// §2.3): manueller Bankstandsabgleich mit dokumentierter Differenz. Die
/// Buchung (<see cref="BookingType.BalanceCorrection"/>) trägt genau ein
/// signiertes Konto-Posting (ungleich 0), keine Pool-Postings und eine
/// Pflicht-Notiz — sie zählt nicht als Einnahme (kein Finanzierungsmonat,
/// keine Verteilung, keine Kategorie) und verändert keine Konto-Pool-
/// Zuordnung (<c>AllocationDelta()</c> bleibt leer). Abgleichdifferenzen
/// werden nicht storniert, sondern durch eine neue dokumentierte
/// Korrektur ersetzt (Design Schritt 6 §3.2).
/// </summary>
public sealed record BalanceCorrectionSpec(
    /// <summary>Stabile Identität der neuen Korrektur-Buchung (Idempotenzschlüssel).</summary>
    string Id,
    /// <summary>Konto, auf dem die dokumentierte Differenz gebucht wird.</summary>
    string AccountId,
    /// <summary>
    /// Signierter Betrag (ungleich 0): positiv = Bankstand höher als
    /// geplant, negativ = niedriger.
    /// </summary>
    Money Amount,
    /// <summary>Stichtag des Abgleichs (Fachdatum).</summary>
    DateOnly Date,
    /// <summary>
    /// Dokumentation der Differenz (Pflicht, z. B. „Bankdifferenz 14.10."
    /// — nicht leer nach Trim).
    /// </summary>
    string Note)
{
    /// <summary>
    /// Prüfungen gegen den Stammdaten-Katalog (Design Schritt 6 §2.3/§3.2):
    /// Id und Konto nicht leer, Betrag ungleich 0 (es gibt nichts zu
    /// dokumentieren), Notiz nicht leer nach Trim (dokumentierte
    /// Differenz ist Pflicht) und Konto existierend und aktiv.
    /// </summary>
    public void Validate(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Kontostandskorrektur ohne Id.");
        }

        if (string.IsNullOrWhiteSpace(AccountId))
        {
            throw new LarioDomainException($"Kontostandskorrektur '{Id}' ohne Konto.");
        }

        if (Amount.Cents == 0)
        {
            throw new LarioDomainException(
                $"Kontostandskorrektur '{Id}': Betrag darf nicht null sein (ist {Amount.ToEuro():0.00} €) — " +
                "ohne Differenz gibt es nichts zu dokumentieren.");
        }

        if (string.IsNullOrWhiteSpace(Note))
        {
            throw new LarioDomainException(
                $"Kontostandskorrektur '{Id}' ohne Notiz — die dokumentierte Differenz ist Pflicht.");
        }

        var account = catalog.FindAccount(AccountId)
            ?? throw new InvalidReferenceException(
                $"Kontostandskorrektur '{Id}' referenziert unbekanntes Konto '{AccountId}'.");

        if (account.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Kontostandskorrektur '{Id}' referenziert das archivierte Konto '{AccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }
    }

    /// <summary>
    /// Baut die Kontostandskorrektur-Buchung (atomare Buchungseinheit):
    /// genau ein Konto-Posting mit dem signierten Betrag, keine
    /// Pool-Postings, Datum und Notiz aus der geprüften Vorgabe.
    /// </summary>
    public Booking ToBooking()
    {
        var accountPostings = new List<AccountPosting>
        {
            new() { AccountId = AccountId, Amount = Amount },
        };

        return new Booking(
            Id,
            BookingType.BalanceCorrection,
            Date,
            Note,
            accountPostings,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }
}
