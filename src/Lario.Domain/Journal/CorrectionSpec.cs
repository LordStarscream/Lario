using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Korrektur-Vorgabe (Rechenvertrag §5.4, Design Schritt 6 §2.3):
/// In-place-Änderung einer bestehenden Ausgabe-, Einnahme- oder
/// Anfangsbestand-Buchung — „keine zweite Ausgabe“ (Plan-Abnahme).
/// Änderbar sind Datum, Notiz, Konto, Gesamtbetrag sowie die Teilposten
/// (Ausgabe), die Verteilung (Einnahme) bzw. die Verteilung und das Konto
/// (Anfangsbestand); <c>null</c> bei einem Feld bedeutet „bleibt
/// unverändert".
/// Die <see cref="OperationId"/> ist der stabile Idempotenzschlüssel der
/// Korrektur-Historie: gleicher Inhalt wird nur quittiert (Revision bleibt
/// unberührt), gleiche Operation-Id mit anderem Inhalt ist ein Konflikt
/// (Rechenvertrag §5.5). Die <see cref="ExpectedBaseRevision"/> gehört zur
/// Änderung; der Abgleich mit der Ist-Revision macht der Service.
/// </summary>
public sealed record CorrectionSpec(
    /// <summary>Stabile Identität der zu korrigierenden Buchung.</summary>
    string BookingId,
    /// <summary>Stabile Operation-Id (Idempotenzschlüssel der Korrektur-Historie).</summary>
    string OperationId,
    /// <summary>Erwartete Basisrevision der Buchung (Pflicht, Rechenvertrag §5.5).</summary>
    int ExpectedBaseRevision,
    /// <summary>Neues Fachdatum; null = bleibt unverändert.</summary>
    DateOnly? Date = null,
    /// <summary>Neue Notiz; null = bleibt unverändert.</summary>
    string? Note = null,
    /// <summary>Neues Konto; null = bleibt unverändert.</summary>
    string? AccountId = null,
    /// <summary>Neuer Gesamtbetrag (positiv); null = bleibt unverändert.</summary>
    Money? TotalAmount = null,
    /// <summary>
    /// Neue Teilposten der Ausgabe (ersetzen die alten); null = unverändert.
    /// <c>tax = null</c> bei einem Teilposten bedeutet „unverändert": ein
    /// bestehender Teilposten (gleiche Id) behält seine ursprüngliche
    /// Steuerkennzeichnung (inkl. Steuer-Notiz und bisheriger
    /// Nichtmarkierung), ein neuer Teilposten wird wie bei der Neuanlage
    /// aus der Kategorie-Voreinstellung materialisiert (Review Runde 2 P2).
    /// </summary>
    IReadOnlyList<ExpenseSplitSpec>? Splits = null,
    /// <summary>Neue Verteilung der Einnahme (ersetzt die alte); null = unverändert.</summary>
    IReadOnlyList<IncomeAllocationSpec>? Allocations = null)
{
    /// <summary>
    /// Prüfungen der Korrektur gegen Stammdaten-Katalog und Original
    /// (Design Schritt 6 §2.3/§3.2):
    /// <para>
    /// - Bezug: <see cref="BookingId"/> == Original-Id; Operation-Id nicht
    ///   leer; erwartete Basisrevision mindestens 1; mindestens eines der
    ///   Felder Datum/Notiz/Konto/Gesamtbetrag/Teilposten/Verteilung ist
    ///   gesetzt (leere Korrektur ist kein Befehl).
    /// </para>
    /// <para>
    /// - Typ: nur Ausgaben, Einnahmen und Anfangsbestände sind korrigierbar
    ///   (Zwischenstep 7.5) — alle anderen Buchungstypen werden über
    ///   Storno + Neuausgabe geändert.
    /// </para>
    /// <para>
    /// - Ausschluss: Teilposten und Verteilung niemals gleichzeitig;
    ///   Teilposten nur bei Ausgaben, Verteilungen nur bei Einnahmen —
    ///   jeweils nur zusammen mit neuem Gesamtbetrag (positiv).
    /// </para>
    /// <para>
    /// - Ersatzinhalte werden wie eine Neuanlage validiert (Katalog:
    ///   aktive Konten/Pools/Kategorien, positive Beträge, eindeutige
    ///   Pools, Summe exakt zum Gesamtbetrag) — über dieselben
    ///   Spezifikationen wie die Erstanlage (<see cref="ExpenseSpec"/>
    ///   bzw. <see cref="IncomeSpec"/>); das wirksame Konto (neu oder
    ///   Original) ist dabei Teil der Prüfung.
    /// </para>
    /// </summary>
    public void Validate(MasterDataCatalog catalog, Booking original)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(original);

        if (string.IsNullOrWhiteSpace(BookingId))
        {
            throw new LarioDomainException("Korrektur ohne Buchung-Id.");
        }

        if (BookingId != original.Id)
        {
            throw new InvalidReferenceException(
                $"Korrektur für Buchung '{BookingId}', das Original trägt die Id '{original.Id}': " +
                "der Bezug der Korrektur ist falsch.");
        }

        if (string.IsNullOrWhiteSpace(OperationId))
        {
            throw new LarioDomainException(
                $"Korrektur der Buchung '{BookingId}' ohne Operation-Id — " +
                "sie ist der Idempotenzschlüssel der Korrektur-Historie.");
        }

        if (ExpectedBaseRevision < 1)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Buchung '{BookingId}': erwartete Basisrevision muss mindestens 1 sein " +
                $"(ist {ExpectedBaseRevision}).");
        }

        if (Date is null && Note is null && AccountId is null && TotalAmount is null
            && Splits is null && Allocations is null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Buchung '{BookingId}' ohne Änderungen — mindestens eines der " +
                "Felder Datum, Notiz, Konto, Gesamtbetrag, Teilposten oder Verteilung ist erforderlich.");
        }

        if (original.Type is not (BookingType.Expense or BookingType.Income or BookingType.OpeningBalance))
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Buchung '{BookingId}': nur Ausgaben, Einnahmen und " +
                $"Anfangsbestände sind korrigierbar (Buchungstyp {original.Type}) — andere Buchungstypen " +
                "ändern Sie über Storno + Neuausgabe.");
        }

        if (Splits is not null && Allocations is not null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Buchung '{BookingId}': Teilposten und Verteilung dürfen " +
                "nicht gleichzeitig angegeben werden.");
        }

        if (original.Type == BookingType.Expense)
        {
            ValidateExpenseCorrection(catalog, original);
        }
        else if (original.Type == BookingType.Income)
        {
            ValidateIncomeCorrection(catalog, original);
        }
        else
        {
            ValidateOpeningBalanceCorrection(catalog, original);
        }
    }

    /// <summary>
    /// Prüft die wirksame Fassung eines korrigierten Anfangsbestands wie
    /// eine Neuanlage (Zwischenstep 7.5): Stichtag, Konto, Gesamtbetrag
    /// (≥ 0) und Verteilung (aktive Pools, eindeutige Zeilen, Summe ≤
    /// Total) — über dieselbe Prüfung wie die Erstanlage
    /// (<see cref="OpeningBalanceSpec.ValidateAgainst"/>). Teilposten sind
    /// ausgeschlossen; <see cref="Allocations"/> tragen die Verteilung.
    /// </summary>
    private void ValidateOpeningBalanceCorrection(MasterDataCatalog catalog, Booking original)
    {
        if (Splits is not null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Buchung '{BookingId}': Teilposten gehören zu " +
                "Ausgaben — ein Anfangsbestand wird über Gesamtbetrag und Verteilung korrigiert.");
        }

        // Wirksame Fassung wie Neuanlage validieren (Muster
        // ExpenseSpec/IncomeSpec): Konto, Stichtag, Betrag ≥ 0, aktive
        // Pools, eindeutige Verteilung und Summe ≤ Gesamtbetrag.
        new OpeningBalanceSpec(
            Id: OperationId,
            Date: Date ?? original.Date,
            AccountId: AccountId ?? original.AccountPostings.Single().AccountId,
            Total: TotalAmount ?? original.AccountPostings.Single().Amount,
            Distribution: Allocations is null
                ? original.PoolPostings
                    .Select(p => new PoolAllocation(p.PoolId, p.Amount))
                    .ToList()
                : Allocations
                    .Select(a => new PoolAllocation(a.PoolId, a.Amount))
                    .ToList(),
            Note: Note ?? original.Note)
            .ValidateAgainst(catalog);
    }

    /// <summary>
    /// Baut die korrigierte Buchung (Design Schritt 6 §2.5): dieselbe
    /// <see cref="Booking.Id"/>, derselbe <see cref="Booking.Type"/> und
    /// dieselbe <see cref="Booking.BaseRevision"/> wie das Original — die
    /// Revisionserhöhung macht der Service zusammen mit dem
    /// Historieneintrag. Datum/Notiz/Konto: gesetzt = neuer Wert, null =
    /// alter Wert. Der Gesamtbetrag kommt aus der (geprüften) Vorgabe oder
    /// bleibt der des Originals; das Konto-Posting ist −Gesamtbetrag
    /// (Ausgabe) bzw. +Gesamtbetrag (Einnahme) am wirksamen Konto.
    /// Pool-Postings: je-Pool-Summe der neuen Teilposten/Verteilung bzw.
    /// unverändert vom Original. Teilposten/Verteilung: aus der Vorgabe
    /// bzw. unverändert vom Original. Bei Einnahmen bleibt der
    /// Finanzierungsmonat unverändert; Reversal- und Refund-Verknüpfungen
    /// werden nicht gesetzt.
    /// <para>
    /// <paramref name="catalog"/> (optional): Materialisierung der
    /// Steuer-Voreinstellung bei neuen Teilposten ohne explizite
    /// Kennzeichnung — dieselbe Regel wie bei der Neuanlage
    /// (<see cref="ExpenseSpec.MaterializeEffectivePreset"/>). Ohne
    /// Katalog bleiben solche Teilposten unmarkiert; explizite
    /// Kennzeichnungen sind davon unberührt.
    /// </para>
    /// <para>
    /// <c>tax = null</c> bei einem Teilposten heißt „unverändert" (dieselbe
    /// Konvention wie die übrigen Korrekturfelder): ein bestehender
    /// Teilposten behält seine ursprüngliche Steuerkennzeichnung vollständig
    /// (inkl. Steuer-Notiz und einer bisherigen Nichtmarkierung — die
    /// aktuelle Kategorie-Voreinstellung wird <i>nicht</i> still neu
    /// angewendet); ein neuer Teilposten wird aus der Voreinstellung
    /// materialisiert (Review Runde 2 P2). Eine Steueränderung muss
    /// daher als explizite Kennzeichnung gesendet werden.
    /// </para>
    /// </summary>
    public Booking BuildUpdatedBooking(Booking original, MasterDataCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (original.Type is not (BookingType.Expense or BookingType.Income or BookingType.OpeningBalance))
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}': Buchung '{original.Id}' (Buchungstyp {original.Type}) ist nicht " +
                "korrigierbar — nur Ausgaben, Einnahmen und Anfangsbestände lassen sich korrigieren.");
        }

        var accountId = AccountId ?? original.AccountPostings.Single().AccountId;
        var date = Date ?? original.Date;
        var note = Note ?? original.Note;
        var total = TotalAmount ?? OriginalTotal(original);

        if (original.Type == BookingType.Expense)
        {
            var accountPostings = new List<AccountPosting>
            {
                new() { AccountId = accountId, Amount = total.Negate() },
            };

            List<PoolPosting> poolPostings;
            List<ExpenseSplit> splits;
            if (Splits is null)
            {
                // Kein neuer Split-Inhalt: Postings und Teilposten bleiben
                // unverändert (Gesamtbetrag und Konto dürfen sich ändern —
                // ohne neue Teilposten bleibt der Gesamtbetrag der
                // Teilposten-Summe erhalten).
                poolPostings = original.PoolPostings.ToList();
                splits = original.Splits.ToList();
            }
            else
            {
                var poolTotals = new Dictionary<string, Money>(StringComparer.Ordinal);
                foreach (var split in Splits)
                {
                    poolTotals[split.PoolId] =
                        poolTotals.GetValueOrDefault(split.PoolId, Money.Zero).Add(split.Amount);
                }

                poolPostings = poolTotals
                    .Select(p => new PoolPosting { PoolId = p.Key, Amount = p.Value.Negate() })
                    .ToList();

                // tax = null bedeutet „unverändert" (dieselbe Konvention wie
                // die übrigen Korrekturfelder, Review Runde 2 P2): ein
                // bestehender Teilposten (gleiche Id) behält seine
                // ursprüngliche Steuerkennzeichnung — auch eine bisherige
                // Nichtmarkierung und eine Steuer-Notiz; nur ein neuer
                // Teilposten wird aus der Kategorie-Voreinstellung
                // materialisiert wie bei der Neuanlage.
                var originalSplitById = original.Splits is { } originalSplits
                    ? originalSplits.ToDictionary(s => s.Id, StringComparer.Ordinal)
                    : null;

                splits = Splits
                    .Select(s =>
                    {
                        TaxMarking? tax;
                        if (s.Tax is { } explicitTax)
                        {
                            tax = explicitTax;
                        }
                        else if (originalSplitById is not null
                                 && originalSplitById.TryGetValue(s.Id, out var originalSplit))
                        {
                            tax = originalSplit.Tax;
                        }
                        else
                        {
                            tax = catalog is null
                                ? null
                                : ExpenseSpec.MaterializeEffectivePreset(catalog, s.CategoryId);
                        }

                        return new ExpenseSplit
                        {
                            Id = s.Id,
                            Amount = s.Amount,
                            PoolId = s.PoolId,
                            CategoryId = s.CategoryId,
                            Note = s.Note,
                            Tax = tax,
                        };
                    })
                    .ToList();
            }

            return new Booking(
                original.Id,
                BookingType.Expense,
                date,
                note,
                accountPostings,
                poolPostings,
                null,
                splits,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        // Anfangsbestand (Zwischenstep 7.5): Konto und Gesamtbetrag dürfen
        // sich ändern; die Verteilung (null = unverändert) ersetzt die
        // Pool-Postings. Das gesicherte Anfangsbestandskonto folgt dem
        // wirksamen Konto (Einmaligkeitsregel je Konto).
        if (original.Type == BookingType.OpeningBalance)
        {
            var openingAccountPostings = new List<AccountPosting>
            {
                new() { AccountId = accountId, Amount = total },
            };

            var openingPoolPostings = Allocations is null
                ? original.PoolPostings.ToList()
                : Allocations
                    .Select(a => new PoolPosting { PoolId = a.PoolId, Amount = a.Amount })
                    .ToList();

            return new Booking(
                original.Id,
                BookingType.OpeningBalance,
                date,
                note,
                openingAccountPostings,
                openingPoolPostings,
                openingBalanceAccountId: accountId);
        }

        // Einnahme: Verteilung ersetzt die Pool-Postings; der
        // Finanzierungsmonat bleibt unverändert; keine Teilposten.
        var incomeAccountPostings = new List<AccountPosting>
        {
            new() { AccountId = accountId, Amount = total },
        };

        var incomePoolPostings = Allocations is null
            ? original.PoolPostings.ToList()
            : Allocations
                .Select(a => new PoolPosting { PoolId = a.PoolId, Amount = a.Amount })
                .ToList();

        return new Booking(
            original.Id,
            BookingType.Income,
            date,
            note,
            incomeAccountPostings,
            incomePoolPostings,
            original.FundingMonth,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    /// <summary>
    /// Gesamtbetrag des Originals als positiver Betrag: Ausgabe = Negation
    /// des negativen Konto-Postings, Einnahme = positives Konto-Posting.
    /// </summary>
    private static Money OriginalTotal(Booking original)
    {
        var accountPosting = original.AccountPostings.Single();
        return original.Type == BookingType.Expense
            ? accountPosting.Amount.Negate()
            : accountPosting.Amount;
    }

    private void ValidateExpenseCorrection(MasterDataCatalog catalog, Booking original)
    {
        if (Allocations is not null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Ausgabe '{BookingId}': Verteilungen gehören zu Einnahmen, " +
                "nicht zu Ausgaben.");
        }

        if (Splits is not null && TotalAmount is null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Ausgabe '{BookingId}': Teilposten werden nur zusammen mit " +
                "neuem Gesamtbetrag angegeben.");
        }

        if (TotalAmount is { } total && total.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Ausgabe '{BookingId}': Gesamtbetrag muss positiv sein " +
                $"(ist {total.ToEuro():0.00} €).");
        }

        if (Splits is null && TotalAmount is { } newTotal && newTotal != OriginalTotal(original))
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Ausgabe '{BookingId}': Gesamtbetrag geändert, aber Teilposten nicht " +
                "angegeben — die Teilposten müssen sich exakt zum Gesamtbetrag addieren (Splitregel).");
        }

        if (Splits is not null)
        {
            // Ersatzinhalte wie Neuanlage validieren (Muster ExpenseSpec):
            // aktive Pools/Kategorien, positive Beträge, eindeutige
            // Teilposten-Ids, Splitregel (Summe == Gesamtbetrag) und das
            // wirksame Konto (neu oder Original) aktiv.
            new ExpenseSpec
            {
                Id = OperationId,
                Date = Date ?? original.Date,
                TotalAmount = TotalAmount!.Value,
                AccountId = AccountId ?? original.AccountPostings.Single().AccountId,
                Splits = Splits,
            }.Validate(catalog);
        }
        else if (AccountId is not null)
        {
            RequireActiveAccount(catalog, AccountId);
        }
    }

    private void ValidateIncomeCorrection(MasterDataCatalog catalog, Booking original)
    {
        if (Splits is not null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Einnahme '{BookingId}': Teilposten gehören zu Ausgaben, " +
                "nicht zu Einnahmen.");
        }

        if (Allocations is not null && TotalAmount is null)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Einnahme '{BookingId}': Verteilungen werden nur zusammen mit " +
                "neuem Gesamtbetrag angegeben.");
        }

        if (TotalAmount is { } total && total.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Einnahme '{BookingId}': Gesamtbetrag muss positiv sein " +
                $"(ist {total.ToEuro():0.00} €).");
        }

        if (Allocations is null && TotalAmount is { } newTotal && newTotal != OriginalTotal(original))
        {
            throw new LarioDomainException(
                $"Korrektur '{OperationId}' der Einnahme '{BookingId}': Gesamtbetrag geändert, aber Verteilung nicht " +
                "angegeben — die Verteilung muss den Gesamtbetrag exakt abdecken.");
        }

        if (Allocations is not null)
        {
            // Ersatzinhalte wie Neuanlage validieren (Muster IncomeSpec):
            // aktive Pools, eindeutige Pools, positive Beträge, Summe der
            // Verteilung == Gesamtbetrag und das wirksame Konto (neu oder
            // Original) aktiv. Der Finanzierungsmonat bleibt unverändert
            // und ist im Original materialisiert (valid).
            new IncomeSpec
            {
                Id = OperationId,
                Date = Date ?? original.Date,
                Amount = TotalAmount!.Value,
                AccountId = AccountId ?? original.AccountPostings.Single().AccountId,
                FundingMonth = original.FundingMonth,
                Allocations = Allocations,
            }.Validate(catalog);
        }
        else if (AccountId is not null)
        {
            RequireActiveAccount(catalog, AccountId);
        }
    }

    private static void RequireActiveAccount(MasterDataCatalog catalog, string accountId)
    {
        var account = catalog.FindAccount(accountId)
            ?? throw new InvalidReferenceException(
                $"Korrektur referenziert unbekanntes Konto '{accountId}'.");

        if (account.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Korrektur referenziert das archivierte Konto '{accountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }
    }
}
