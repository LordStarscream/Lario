using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Steuerliche Kennzeichnung eines Teilpostens (Plan §9): steuerlich
/// relevant ja/nein, optional eine (Unter-)Steuerkategorie, optional ein
/// gekennzeichneter Teilbetrag innerhalb des Splitbetrags und optional
/// eine Notiz.
///
/// Regeln:
/// - Die Kennzeichnung ist unabhängig von Konto, Pool und normaler
///   Ausgabenkategorie und verändert <b>keinen</b> Saldo und keinen
///   Richtwert — sie ist reine Angabe, keine Berechnung.
/// - <c>Relevant = false</c>: nichts ist gekennzeichnet — weder eine
///   Steuerkategorie noch ein Teilbetrag sind zulässig.
/// - <c>Relevant = true</c>: die Steuerkategorie ist optional (relevante
///   Posten ohne gewählte Steuerkategorie sind in der Auswertung in einer
///   eigenen Liste sichtbar); der Teilbetrag ist optional, null bedeutet
///   „gesamter Splitbetrag markiert“. Ist er gesetzt, darf er den
///   Splitbetrag nicht übersteigen.
/// - Bei Erstellung wird eine gewählte Steuerkategorie geprüft
///   (existierend und aktiv — neue Referenz; bereits verwendete
///   Steuerkategorien werden archiviert statt gelöscht, historische
///   Kennzeichnungen behalten ihren Verweis).
/// - Bei Erstellung ohne explizite Kennzeichnung übernimmt der Split die
///   wirksame Voreinstellung seiner Kategorie (Plan §9); die Kennzeichnung
///   ist danach ein Snapshot im Split — spätere Voreinstellungsänderungen
///   schreiben historische Kennzeichnungen nicht um.
/// </summary>
public sealed class TaxMarking
{
    /// <summary>
    /// Steuerlich relevant (ja/nein). Setbar, damit das Repository die
    /// Kennzeichnung einer In-place-Korrektur in der vorhandenen Zeile
    /// aktualisieren kann (fachliche Invarianten gelten an der
    /// Service-Grenze, nicht in der Eigenschaft).
    /// </summary>
    public bool Relevant { get; set; }

    /// <summary>
    /// Gewählte (Unter-)Steuerkategorie; null = keine gewählt.
    /// Nur bei <see cref="Relevant"/> = true zulässig.
    /// </summary>
    public string? TaxCategoryId { get; set; }

    /// <summary>
    /// Gekennzeichneter Teilbetrag in Cent; null = gesamter Splitbetrag
    /// markiert. Ist er gesetzt, muss 0 ≤ Teilbetrag ≤ Splitbetrag gelten.
    /// </summary>
    public Money? MarkedAmount { get; set; }

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// True, wenn die Kennzeichnung bei der Erstellung nicht explizit
    /// angegeben, sondern aus der wirksamen Kategorie-Voreinstellung
    /// materialisiert wurde (Snapshot). Eine explizit übergebene
    /// Kennzeichnung trägt false (wird bei <c>ToBooking</c>
    /// normalisiert — ein vom Aufrufer gesetztes Flag wird nicht
    /// übernommen).
    ///
    /// Die Herkunft ist Teil des Original-Eingabeinhalts und wird für
    /// die Idempotenz gebraucht: „keine Angabe“ und „explizit angegeben"
    /// sind verschiedene Eingaben — auch wenn das Ergebnis gleich sein
    /// kann (Voreinstellung ≙ explizite Kennzeichnung).
    /// </summary>
    public bool InheritedFromPreset { get; set; }

    /// <summary>
    /// Prüfungen der Kennzeichnung selbst (ohne Kontext):
    /// <c>Relevant = false</c> erlaubt weder Steuerkategorie noch
    /// Teilbetrag; ein gesetzter Teilbetrag ist nicht negativ.
    /// </summary>
    public void ValidateSelf()
    {
        if (MarkedAmount is { } marked && marked.Cents < 0)
        {
            throw new LarioDomainException(
                $"Gekennzeichneter Teilbetrag ist negativ ({marked.ToEuro():0.00} €).");
        }

        if (!Relevant)
        {
            if (TaxCategoryId is not null)
            {
                throw new LarioDomainException(
                    "Nicht steuerlich relevanter Teilposten trägt eine Steuerkategorie.");
            }

            if (MarkedAmount is not null)
            {
                throw new LarioDomainException(
                    "Nicht steuerlich relevanter Teilposten trägt einen gekennzeichneten Teilbetrag.");
            }
        }
    }

    /// <summary>
    /// Prüfungen gegen den Stammdaten-Katalog und den Splitbetrag:
    /// eine gewählte Steuerkategorie muss existieren und aktiv sein;
    /// der Teilbetrag darf den Splitbetrag nicht übersteigen.
    /// </summary>
    public void Validate(MasterDataCatalog catalog, Money splitAmount)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ValidateSelf();

        if (TaxCategoryId is not null)
        {
            var taxCategory = catalog.FindTaxCategory(TaxCategoryId)
                ?? throw new InvalidReferenceException(
                    $"Steuerkennzeichnung referenziert unbekannte Steuerkategorie '{TaxCategoryId}'.");

            if (taxCategory.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Steuerkennzeichnung referenziert die archivierte Steuerkategorie '{TaxCategoryId}': " +
                    "neue Kennzeichnungen dürfen nur aktive Steuerkategorien wählen.");
            }
        }

        if (MarkedAmount is { } marked && marked.Cents > splitAmount.Cents)
        {
            throw new LarioDomainException(
                $"Gekennzeichneter Teilbetrag ({marked.ToEuro():0.00} €) übersteigt " +
                $"den Splitbetrag ({splitAmount.ToEuro():0.00} €).");
        }
    }

    public override string ToString()
    {
        if (!Relevant)
        {
            return "nicht relevant";
        }

        var category = TaxCategoryId ?? "keine";
        var part = MarkedAmount is { } marked ? $"{marked.ToEuro():0.00} €" : "voll";
        return $"relevant (Kategorie: {category}, Teil: {part})";
    }
}
