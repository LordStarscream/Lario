using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Anfangsbestand (Schritt 3, Rechenvertrag §3.7): Stichtag, explizite
/// Pool-Verteilung, sichtbarer Rest „noch nicht zugeordnet", keine
/// Einnahme. Referenzzahlen aus Plan §5 Schritt 3 („Prüfen").
/// </summary>
public class OpeningBalanceSpecTests
{
    private static MasterDataCatalog Catalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        return catalog;
    }

    [Fact(DisplayName = "Plan-Kernfall: 1.000 € = 700 € Flex + 300 € Freizeit, kein Rest")]
    public void FullDistribution_NoUnallocated()
    {
        var spec = new OpeningBalanceSpec(
            "init-2026-01-01",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            Money.FromCents(100_000),
            [
                new PoolAllocation("pool-flex", Money.FromCents(70_000)),
                new PoolAllocation("pool-freizeit", Money.FromCents(30_000)),
            ]);

        spec.ValidateAgainst(Catalog());

        Assert.Equal(Money.FromCents(100_000), spec.AllocatedTotal);
        Assert.Equal(Money.Zero, spec.Unallocated);

        var booking = spec.ToBooking();
        Assert.Equal(BookingType.OpeningBalance, booking.Type);
        Assert.Single(booking.AccountPostings);
        Assert.Equal(Money.FromCents(100_000), booking.AccountPostings[0].Amount);
        Assert.Equal(2, booking.PoolPostings.Count);
        Assert.Equal(1, booking.BaseRevision);
    }

    [Fact(DisplayName = "Teilweise Zuordnung lässt sichtbaren Rest 'noch nicht zugeordnet'")]
    public void PartialDistribution_LeavesVisibleRemainder()
    {
        var spec = new OpeningBalanceSpec(
            "init-1",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            Money.FromCents(100_000),
            [new PoolAllocation("pool-flex", Money.FromCents(70_000))]);

        spec.ValidateAgainst(Catalog());
        Assert.Equal(Money.FromCents(30_000), spec.Unallocated);

        var booking = spec.ToBooking();
        // Der Rest ist keine Poolposting — er bleibt als Ableitung sichtbar.
        Assert.Single(booking.PoolPostings);

        var journal = new BookingJournal([booking]);
        Assert.Equal(Money.FromCents(30_000), journal.OpeningBalanceUnallocated("init-1"));
        // Kontostand 1.000 €, Pool 700 € → Lücke 300 € erklärt den Rest.
        Assert.Equal(Money.FromCents(30_000), journal.AccountPoolGap());
    }

    [Fact(DisplayName = "Verteilung über den Gesamtbetrag wird abgelehnt")]
    public void OverAllocation_IsRejected()
    {
        var spec = new OpeningBalanceSpec(
            "init-1",
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "acc-haupt",
            Money.FromCents(100_000),
            [new PoolAllocation("pool-flex", Money.FromCents(100_001))]);

        Assert.Throws<LarioDomainException>(() => spec.ValidateAgainst(Catalog()));
    }

    [Fact(DisplayName = "Negativer Gesamtbetrag und negativer Teilbetrag werden abgelehnt")]
    public void NegativeAmounts_AreRejected()
    {
        var catalog = Catalog();

        var negativeTotal = new OpeningBalanceSpec(
            "init-neg", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-haupt",
            Money.FromCents(-1), []);
        Assert.Throws<LarioDomainException>(() => negativeTotal.ValidateAgainst(catalog));

        var negativeLine = new OpeningBalanceSpec(
            "init-neg2", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-haupt",
            Money.OneEuro, [new PoolAllocation("pool-flex", Money.FromCents(-1))]);
        Assert.Throws<LarioDomainException>(() => negativeLine.ValidateAgainst(catalog));
    }

    [Fact(DisplayName = "Unbekanntes Konto, unbekannter Pool und doppelte Pool-Zeile werden abgelehnt")]
    public void InvalidReferences_AreRejected()
    {
        var catalog = Catalog();

        var unknownAccount = new OpeningBalanceSpec(
            "init-1", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-keins",
            Money.OneEuro, []);
        Assert.Throws<InvalidReferenceException>(() => unknownAccount.ValidateAgainst(catalog));

        var unknownPool = new OpeningBalanceSpec(
            "init-2", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-haupt",
            Money.OneEuro, [new PoolAllocation("pool-keins", Money.OneEuro)]);
        Assert.Throws<InvalidReferenceException>(() => unknownPool.ValidateAgainst(catalog));

        var duplicatePool = new OpeningBalanceSpec(
            "init-3", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-haupt",
            Money.OneEuro,
            [
                new PoolAllocation("pool-flex", Money.FromCents(50)),
                new PoolAllocation("pool-flex", Money.FromCents(50)),
            ]);
        Assert.Throws<LarioDomainException>(() => duplicatePool.ValidateAgainst(catalog));
    }

    [Fact(DisplayName = "Inhaltliche Gleichheit erkennt Änderungen (für die Konfliktprüfung)")]
    public void ContentEquality_DetectsChanges()
    {
        var baseSpec = new OpeningBalanceSpec(
            "init-1", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-haupt",
            Money.FromCents(100_000),
            [new PoolAllocation("pool-flex", Money.FromCents(70_000))],
            "Notiz");

        Assert.True(baseSpec.HasSameContentAs(baseSpec));

        var differentAmount = baseSpec with
        {
            Total = Money.FromCents(99_999),
        };
        Assert.False(baseSpec.HasSameContentAs(differentAmount));

        var differentPool = new OpeningBalanceSpec(
            "init-1", DateOnly.FromDateTime(new DateTime(2026, 1, 1)), "acc-haupt",
            Money.FromCents(100_000),
            [new PoolAllocation("pool-freizeit", Money.FromCents(70_000))],
            "Notiz");
        Assert.False(baseSpec.HasSameContentAs(differentPool));
    }
}

/// <summary>
/// Saldenrekonstruktion aus dem Journal (Rechenvertrag §3.2/§3.4):
/// Zeilensummen = Kontostände, Spaltensummen = Poolbestände;
/// Anfangsbestand ist keine Einnahme.
/// </summary>
public class BookingJournalTests
{
    [Fact(DisplayName = "Rechenvertrag-Fall 1: 100.000 Flex + 30.000 Freizeit, Summen 130.000")]
    public void ReferenceCase_OpeningBalances_ReconstructBalances()
    {
        var main = new Booking(
            "init-2026-01-01",
            BookingType.OpeningBalance,
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "Anfangsbestand",
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(100_000) }]);

        var leisure = new Booking(
            "init-2026-01-01-freizeit",
            BookingType.OpeningBalance,
            DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            "Anfangsbestand",
            [new AccountPosting { AccountId = "acc-freizeit", Amount = Money.FromCents(30_000) }],
            [new PoolPosting { PoolId = "pool-freizeit", Amount = Money.FromCents(30_000) }]);

        var journal = new BookingJournal([main, leisure]);

        Assert.Equal(Money.FromCents(100_000), journal.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(30_000), journal.AccountBalance("acc-freizeit"));
        Assert.Equal(Money.Zero, journal.AccountBalance("acc-spar"));
        Assert.Equal(Money.FromCents(100_000), journal.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(30_000), journal.PoolBalance("pool-freizeit"));
        Assert.Equal(Money.FromCents(130_000), journal.TotalAccountBalance());
        Assert.Equal(Money.FromCents(130_000), journal.TotalPoolBalance());
        Assert.Equal(Money.Zero, journal.AccountPoolGap());

        // Anfangsbestände sind keine Einnahmen.
        Assert.Equal(Money.Zero, journal.TotalIncome());
        Assert.Equal(2, journal.OpeningBalances().Count);
    }

    [Fact(DisplayName = "Buchung fasst dasselbe Konto/Pool nicht doppelt auf")]
    public void Booking_DuplicatePosting_IsRejected()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "b-1", BookingType.OpeningBalance, DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            null,
            [
                new AccountPosting { AccountId = "acc-1", Amount = Money.OneEuro },
                new AccountPosting { AccountId = "acc-1", Amount = Money.OneEuro },
            ],
            []));

        Assert.Throws<LarioDomainException>(() => new Booking(
            "b-2", BookingType.OpeningBalance, DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
            null,
            [],
            [
                new PoolPosting { PoolId = "pool-1", Amount = Money.OneEuro },
                new PoolPosting { PoolId = "pool-1", Amount = Money.OneEuro },
            ]));
    }

    [Fact(DisplayName = "Signed Postings: Belastung und Erhöhung heben sich korrekt auf")]
    public void SignedPostings_NetCorrectly()
    {
        var journal = new BookingJournal([
            new Booking(
                "b-1", BookingType.OpeningBalance, DateOnly.FromDateTime(new DateTime(2026, 1, 1)),
                null,
                [new AccountPosting { AccountId = "acc-1", Amount = Money.FromCents(500) }],
                [new PoolPosting { PoolId = "pool-1", Amount = Money.FromCents(500) }]),
            // Simulierte spätere Ausgabe (Schritt 4): Konto −85, Pool −85.
            new Booking(
                "b-2", BookingType.Expense, DateOnly.FromDateTime(new DateTime(2026, 1, 15)),
                null,
                [new AccountPosting { AccountId = "acc-1", Amount = Money.FromCents(-85) }],
                [new PoolPosting { PoolId = "pool-1", Amount = Money.FromCents(-85) }]),
        ]);

        Assert.Equal(Money.FromCents(415), journal.AccountBalance("acc-1"));
        Assert.Equal(Money.FromCents(415), journal.PoolBalance("pool-1"));
        Assert.Equal(Money.Zero, journal.AccountPoolGap());
    }
}
