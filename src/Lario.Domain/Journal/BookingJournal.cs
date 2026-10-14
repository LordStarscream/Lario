namespace Lario.Domain.Journal;

/// <summary>
/// Saldenabfragen über das Buchungsjournal (Rechenvertrag §3.2:
/// „Abfragen berechnen Salden aus den Grunddaten. Es gibt keine
/// gesondert gepflegten Saldo-Felder. Abgeleitete Salden müssen jederzeit
/// rekonstruierbar bleiben").
///
/// Der Journal-Stand ist ein Snapshot (Liste aller Buchungen); Abfragen
/// sind deterministisch und wiederholbar.
///
/// Signierte Konto-Pool-Zuordnung (Rechenvertrag §3.4): die Matrix wird
/// aus den Postings abgeleitet (<see cref="Allocations"/>, je Buchung
/// <see cref="Booking.AllocationDelta"/>). Invarianten: Zeilensumme je
/// Account = Kontostand (+ nicht zugeordneter Anfangsbestands-Rest),
/// Spaltensumme je Pool = Poolbestand. Negative Zellen sind interne
/// Vorfinanzierungen und werden über <see cref="DeriveSettlementPositions"/>
/// deterministisch in Ausgleichspositionen überführt (§3.5).
/// </summary>
public sealed class BookingJournal
{
    private readonly IReadOnlyList<Booking> _bookings;
    private Dictionary<(string AccountId, string PoolId), Money>? _allocationCells;

    public BookingJournal(IEnumerable<Booking> bookings)
    {
        _bookings = bookings?.ToList() ?? [];
    }

    /// <summary>Alle Buchungen im Journal.</summary>
    public IReadOnlyList<Booking> Bookings => _bookings;

    /// <summary>Kontostand: Zeilensumme der AccountPostings des Kontos.</summary>
    public Money AccountBalance(string accountId) =>
        _bookings
            .SelectMany(b => b.AccountPostings)
            .Where(p => p.AccountId == accountId)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>Poolbestand: Spaltensumme der PoolPostings des Pools.</summary>
    public Money PoolBalance(string poolId) =>
        _bookings
            .SelectMany(b => b.PoolPostings)
            .Where(p => p.PoolId == poolId)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>
    /// Wirksamer Storno-Status einer Buchung (Review R1 P2): ein Storno
    /// einer Buchung macht das Original wirkungslos, ein Storno des Stornos
    /// (Widerruf) macht es wieder wirksam, usw. Eine Buchung, auf die sich
    /// kein Storno bezieht, ist nie storniert.
    ///
    /// Die Historie kann mehrere Stornos derselben Buchung tragen (Storno,
    /// Widerruf, erneutes Storno …). Geprüft wird daher nicht nur die erste
    /// Kette, sondern die Parität aller wirksamen Stornos: Ein Storno wirkt,
    /// solange es nicht selbst wirksam widerrufen ist. Die Anwendung lässt
    /// höchstens ein wirksames Storno je Buchung zu (Doppelstorno ist ein
    /// Konflikt), die Parität bleibt aber die robuste Form über die ganze
    /// Storno-Historie. Terminiert immer: jede Storno-Buchung trägt eine neue,
    /// eindeutige Id, und jede Buchung verweist auf höchstens ein Original
    /// (kein Zyklus).
    /// </summary>
    public bool IsEffectivelyReversed(string bookingId)
    {
        var effectiveReversals = _bookings
            .Where(b => b.Type == BookingType.Reversal && b.ReversesBookingId == bookingId)
            .Count(reversal => !IsEffectivelyReversed(reversal.Id));
        return effectiveReversals % 2 == 1;
    }

    /// <summary>
    /// Direkte Stornos einer Buchung (wirksam und historisch, in
    /// Journal-Reihenfolge). Die Parität von
    /// <see cref="IsEffectivelyReversed"/> allein kann einen zweiten
    /// wirksamen Storno derselben Buchung nicht erkennen (zwei wirksame
    /// Stornos parieren sich zu „nicht storniert“ — das Saldo-Verschieben
    /// bleibt). Die Anwendung erzwingt daher die strengere Struktur: höchstens
    /// ein wirksames Storno je Buchung (Review R2 P1, Schritt 6).
    /// </summary>
    public IReadOnlyList<Booking> ReversalsOf(string bookingId) =>
        _bookings
            .Where(b => b.Type == BookingType.Reversal && b.ReversesBookingId == bookingId)
            .ToList();

    /// <summary>Summe aller Kontostände (Gesamtvermögen).</summary>
    public Money TotalAccountBalance() =>
        _bookings
            .SelectMany(b => b.AccountPostings)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>Summe aller Poolbestände.</summary>
    public Money TotalPoolBalance() =>
        _bookings
            .SelectMany(b => b.PoolPostings)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>
    /// Normale Einnahmen (Buchungstyp <see cref="BookingType.Income"/>).
    /// Anfangsbestände sind keine Einnahmen und zählen hier nicht.
    /// </summary>
    public Money TotalIncome() =>
        _bookings
            .Where(b => b.Type == BookingType.Income)
            .SelectMany(b => b.AccountPostings)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>
    /// Nicht zugeordneter Rest eines Anfangsbestands (Systemposition
    /// „noch nicht zugeordnet"): Total − Summe der PoolPostings. Bleibt
    /// sichtbar und zählt nicht automatisch zu Flex.
    /// </summary>
    public Money OpeningBalanceUnallocated(string openingBalanceId)
    {
        var booking = _bookings.FirstOrDefault(b => b.Id == openingBalanceId)
            ?? throw new InvalidReferenceException(
                $"Unbekannter Anfangsbestand '{openingBalanceId}'.");

        if (booking.Type != BookingType.OpeningBalance)
        {
            throw new LarioDomainException(
                $"Buchung '{openingBalanceId}' ist kein Anfangsbestand (Typ {booking.Type}).");
        }

        var total = booking.AccountPostings
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));
        var allocated = booking.PoolPostings
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

        return total.Subtract(allocated);
    }

    /// <summary>Alle Anfangsbestände des Journals.</summary>
    public IReadOnlyList<Booking> OpeningBalances() =>
        _bookings.Where(b => b.Type == BookingType.OpeningBalance).ToList();

    /// <summary>
    /// Invariante der signierten Konto-Pool-Zuordnung (Rechenvertrag §3.4):
    /// Summe der Kontostände = Summe der Poolbestände. Für Anfangsbestände
    /// gilt das nur, wenn die Verteilung vollständig ist — der nicht
    /// zugeordnete Rest erklärt die Differenz.
    /// </summary>
    public Money AccountPoolGap() =>
        TotalAccountBalance().Subtract(TotalPoolBalance());

    /// <summary>
    /// Signierte Konto-Pool-Zuordnung (Rechenvertrag §3.4): die vollständige
    /// Matrix, abgeleitet aus allen Buchungen des Journals. Zellen mit
    /// Wert 0 werden weggelassen (implizit 0, sie tragen keinen
    /// Sachverhalt). Deterministische Reihenfolge: Konto, dann Pool.
    /// </summary>
    public IReadOnlyList<AccountPoolCell> Allocations() =>
        AllocationCells()
            .Where(c => c.Value.Cents != 0)
            .OrderBy(c => c.Key.AccountId, StringComparer.Ordinal)
            .ThenBy(c => c.Key.PoolId, StringComparer.Ordinal)
            .Select(c => new AccountPoolCell(c.Key.AccountId, c.Key.PoolId, c.Value))
            .ToList();

    /// <summary>
    /// Einzelne Zelle der Zuordnung; 0, wenn die Zelle keinen Sachverhalt
    /// trägt.
    /// </summary>
    public Money Allocation(string accountId, string poolId) =>
        AllocationCells().GetValueOrDefault((accountId, poolId), Money.Zero);

    /// <summary>
    /// Nicht zugeordnete Reste der Anfangsbestände je Konto (Systemposition
    /// „noch nicht zugeordnet", Rechenvertrag §3.7). Nur Konten mit
    /// Restbetrag &gt; 0; deterministische Reihenfolge nach Konto.
    /// </summary>
    public IReadOnlyList<(string AccountId, Money Amount)> UnallocatedOpeningRemainders()
    {
        var remainders = new Dictionary<string, Money>(StringComparer.Ordinal);
        foreach (var booking in OpeningBalances())
        {
            var account = booking.AccountPostings.Single().AccountId;
            var remainder = OpeningBalanceUnallocated(booking.Id);
            if (remainder.Cents > 0)
            {
                remainders[account] = remainders.GetValueOrDefault(account, Money.Zero).Add(remainder);
            }
        }

        return remainders
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => (r.Key, r.Value))
            .ToList();
    }

    /// <summary>
    /// Invarianten der signierten Konto-Pool-Zuordnung (Rechenvertrag §3.4)
    /// als Prüfungsliste:
    /// <para>
    /// - <b>Zeilensumme je Account = Kontostand</b> abzüglich des nicht
    ///   zugeordneten Anfangsbestands-Rests des Kontos (der Rest liegt im
    ///   Kontostand, trägt aber keine Zelle, Rechenvertrag §3.7) und der
    ///   Reste aus Kontostandskorrekturen (die nur den Kontostand
    ///   korrigieren, ohne Zelle, Design Schritt 6 §2.6).
    /// - <b>Spaltensumme je Pool = Poolbestand.</b>
    /// </para>
    /// Leere Liste = konsistent; jede Verletzung ist eine Meldung mit den
    /// betroffenen Werten.
    /// </summary>
    public IReadOnlyList<string> AllocationInvariantViolations()
    {
        var violations = new List<string>();
        var cells = AllocationCells();

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var booking in _bookings)
        {
            foreach (var posting in booking.AccountPostings)
            {
                accounts.Add(posting.AccountId);
            }
        }

        foreach (var (account, _) in cells.Keys)
        {
            accounts.Add(account);
        }

        foreach (var account in accounts.OrderBy(a => a, StringComparer.Ordinal))
        {
            var rowSum = cells
                .Where(c => c.Key.AccountId == account)
                .Aggregate(Money.Zero, (sum, c) => sum.Add(c.Value));
            // Nicht zugeteilter Rest des Kontos: nicht zugeordneter
            // Anfangsbestands-Rest plus Rest aus Kontostandskorrekturen.
            // Beide liegen im Kontostand, tragen aber keine Konto-Pool-Zelle.
            var expected = AccountBalance(account)
                .Subtract(UnallocatedRemainderOf(account))
                .Subtract(BalanceCorrectionRemainderOf(account));
            if (rowSum != expected)
            {
                violations.Add(
                    $"Zeilensumme Konto '{account}' ({rowSum.ToEuro():0.00} €) ≠ Kontostand " +
                    $"abzüglich nicht zugeordnetem Rest — Anfangsbestand " +
                    $"({UnallocatedRemainderOf(account).ToEuro():0.00} €) und " +
                    $"Kontostandskorrekturen ({BalanceCorrectionRemainderOf(account).ToEuro():0.00} €).");
            }
        }

        var pools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var booking in _bookings)
        {
            foreach (var posting in booking.PoolPostings)
            {
                pools.Add(posting.PoolId);
            }
        }

        foreach (var (_, pool) in cells.Keys)
        {
            pools.Add(pool);
        }

        foreach (var pool in pools.OrderBy(p => p, StringComparer.Ordinal))
        {
            var columnSum = cells
                .Where(c => c.Key.PoolId == pool)
                .Aggregate(Money.Zero, (sum, c) => sum.Add(c.Value));
            var expected = PoolBalance(pool);
            if (columnSum != expected)
            {
                violations.Add(
                    $"Spaltensumme Pool '{pool}' ({columnSum.ToEuro():0.00} €) ≠ Poolbestand " +
                    $"({expected.ToEuro():0.00} €).");
            }
        }

        return violations;
    }

    /// <summary>
    /// Ausgleichspositionen, deterministisch aus dem Journal abgeleitet
    /// (Rechenvertrag §3.5): je Zelle (Konto, Pool) eine Episode pro
    /// negativer Phase. Die Buchungen werden in der kanonischen
    /// Journal-Reihenfolge (Fachdatum, dann Id) verarbeitet:
    /// <para>
    /// - Wird eine Zelle negativ bzw. negativer, entsteht eine Position
    ///   bzw. die offene Position wird vertieft (Rest und Gesamtbetrag
    ///   wachsen um die Zunahme der Schuld).
    /// - Macht eine Buchung eine negative Zelle weniger negativ, sinkt der
    ///   Rest (Teilbegleichung); bei Rest 0 wird die Position beglichen
    ///   (settledBy = die Buchung). Eine darüber hinaus positive Zelle
    ///   erzeugt keine neue Position — sie ist normale Zuordnung.
    /// </para>
    /// Ergebnis: alle Episoden (offen und beglichen) in der Reihenfolge
    /// ihrer Entstehung. Die Summe der offenen Restbeträge einer Zelle
    /// entspricht exakt max(0, −Zuordnung) — die negative Zuordnung wird
    /// nicht als vorhandenes Geld angezeigt (§3.4).
    /// </summary>
    public IReadOnlyList<SettlementPositionState> DeriveSettlementPositions()
    {
        var cells = new Dictionary<(string AccountId, string PoolId), Money>(
            new AllocationCellKeyComparer());
        var episodes = new List<SettlementPositionState>();
        var openEpisodeIndex = new Dictionary<(string AccountId, string PoolId), int>(
            new AllocationCellKeyComparer());

        var bookings = _bookings
            .OrderBy(b => b.Date)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var booking in bookings)
        {
            // Je Buchung zuerst je Zelle summieren (eine Buchung darf
            // dieselbe Zelle mehrfach berühren).
            var deltas = new Dictionary<(string AccountId, string PoolId), Money>(
                new AllocationCellKeyComparer());
            foreach (var delta in booking.AllocationDelta())
            {
                var key = (delta.AccountId, delta.PoolId);
                deltas[key] = deltas.GetValueOrDefault(key, Money.Zero).Add(delta.Amount);
            }

            foreach (var (key, delta) in deltas)
            {
                if (delta.Cents == 0)
                {
                    continue;
                }

                var before = cells.GetValueOrDefault(key, Money.Zero);
                var after = before.Add(delta);
                cells[key] = after;

                var debtBefore = before.Cents < 0 ? before.Negate() : Money.Zero;
                var debtAfter = after.Cents < 0 ? after.Negate() : Money.Zero;

                if (delta.Cents < 0)
                {
                    // Zelle sinkt: die Schuld kann nur wachsen.
                    var increase = debtAfter.Subtract(debtBefore);
                    if (increase.Cents <= 0)
                    {
                        continue;
                    }

                    if (openEpisodeIndex.TryGetValue(key, out var index))
                    {
                        var open = episodes[index];
                        episodes[index] = open with
                        {
                            Amount = open.Amount.Add(increase),
                            RestAmount = open.RestAmount.Add(increase),
                        };
                    }
                    else
                    {
                        openEpisodeIndex[key] = episodes.Count;
                        episodes.Add(new SettlementPositionState(
                            key.AccountId,
                            key.PoolId,
                            increase,
                            increase,
                            SettlementStatus.Offen,
                            booking.Id,
                            booking.Date,
                            null,
                            null));
                    }
                }
                else if (openEpisodeIndex.TryGetValue(key, out var openIndex))
                {
                    // Zelle steigt: die Schuld kann nur schrumpfen.
                    var decrease = debtBefore.Subtract(debtAfter);
                    if (decrease.Cents <= 0)
                    {
                        continue;
                    }

                    var open = episodes[openIndex];
                    var newRest = open.RestAmount.Subtract(decrease);
                    if (newRest.Cents == 0)
                    {
                        episodes[openIndex] = open with
                        {
                            RestAmount = Money.Zero,
                            Status = SettlementStatus.Beglichen,
                            SettledByBookingId = booking.Id,
                            SettledDate = booking.Date,
                        };
                        openEpisodeIndex.Remove(key);
                    }
                    else
                    {
                        episodes[openIndex] = open with { RestAmount = newRest };
                    }
                }
            }
        }

        return episodes;
    }

    /// <summary>
    /// Zellen der Zuordnung, einschließlich Null-Zellen; intern
    /// (lazy) aus allen Buchungen abgeleitet.
    /// </summary>
    private Dictionary<(string AccountId, string PoolId), Money> AllocationCells()
    {
        if (_allocationCells is not null)
        {
            return _allocationCells;
        }

        var cells = new Dictionary<(string AccountId, string PoolId), Money>(
            new AllocationCellKeyComparer());
        foreach (var booking in _bookings)
        {
            foreach (var delta in booking.AllocationDelta())
            {
                var key = (delta.AccountId, delta.PoolId);
                cells[key] = cells.GetValueOrDefault(key, Money.Zero).Add(delta.Amount);
            }
        }

        return _allocationCells = cells;
    }

    private Money UnallocatedRemainderOf(string accountId) =>
        UnallocatedOpeningRemainders()
            .Where(r => r.AccountId == accountId)
            .Aggregate(Money.Zero, (sum, r) => sum.Add(r.Amount));

    /// <summary>
    /// Rest aus Kontostandskorrekturen des Kontos (Schritt 6 §2.6): die
    /// Kontostandskorrektur trägt genau ein Konto-Posting und KEIN
    /// Pool-Posting — der korrigierte Betrag liegt im Kontostand, trägt
    /// aber keine Konto-Pool-Zelle. Wie der nicht zugeordnete
    /// Anfangsbestands-Rest ist er Teil des „nicht zugeteilten" Rests, der
    /// die Zeilensummen-Invariante ausgleicht.
    /// </summary>
    private Money BalanceCorrectionRemainderOf(string accountId) =>
        _bookings
            .Where(b => b.Type == BookingType.BalanceCorrection)
            .SelectMany(b => b.AccountPostings)
            .Where(p => p.AccountId == accountId)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    private sealed class AllocationCellKeyComparer : IEqualityComparer<(string AccountId, string PoolId)>
    {
        public bool Equals((string AccountId, string PoolId) x, (string AccountId, string PoolId) y) =>
            x.AccountId == y.AccountId && x.PoolId == y.PoolId;

        public int GetHashCode((string AccountId, string PoolId) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.AccountId),
                StringComparer.Ordinal.GetHashCode(obj.PoolId));
    }
}
