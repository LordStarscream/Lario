namespace Lario.Domain.Journal;

/// <summary>
/// Buchungstypen des Journals (Rechenvertrag §3.2). Der vollständige
/// Satz ist bereits in Schritt 0 festgelegt; die Wirkungslogik der
/// einzelnen Typen wird in den jeweiligen Schritten umgesetzt
/// (Anfangsbestand in Schritt 3, Einnahmen/Ausgaben in Schritt 4,
/// Transfers in Schritt 5, …).
/// </summary>
public enum BookingType
{
    /// <summary>Tatsächliche Einnahme (Eingang am Konto + Zuweisung an Pool).</summary>
    Income = 1,

    /// <summary>Tatsächliche Ausgabe (Abgang vom Konto − Verbrauch je Split).</summary>
    Expense = 2,

    /// <summary>Reiner Kontotransfer: Account− (Quelle), Account+ (Ziel); Pools unverändert.</summary>
    AccountTransfer = 3,

    /// <summary>Umwidmung: Konten unverändert, Pool− (Quelle), Pool+ (Ziel).</summary>
    Reassignment = 4,

    /// <summary>Kombination aus Transfer und Umwidmung (eigener Vorgangstyp, Schritt 5).</summary>
    TransferReassignment = 5,

    /// <summary>Reservierung: Konten und Poolbestand unverändert; frei verfügbarer Anteil sinkt (Schritt 14).</summary>
    Reservation = 6,

    /// <summary>Abschluss/Abbruch eines Vorhabens: Rest wird im Herkunftspool frei (Schritt 14).</summary>
    Closeout = 7,

    /// <summary>Gegenbewegung zu einer Buchung, verknüpft mit der stornierten Buchung (Schritt 6).</summary>
    Reversal = 8,

    /// <summary>Erstattung am Erstattungstag mit Rückfluss zum ursprünglichen Pool (Schritt 6).</summary>
    Refund = 9,

    /// <summary>Dokumentierte Kontostandskorrektur mit expliziter Zuordnung (Schritt 6).</summary>
    BalanceCorrection = 10,

    /// <summary>
    /// Anfangsbestand mit Stichtag und expliziter Pool-Verteilung (Schritt 3).
    /// Keine Einnahme: kein Finanzierungsmonat, keine Deckungsberechnung.
    /// </summary>
    OpeningBalance = 11,
}
