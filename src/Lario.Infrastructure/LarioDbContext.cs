using Lario.Domain;
using Lario.Domain.Attachments;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// EF-Core-Modell für die lokale Lario-Datenbank (SQLite, Schritt 3).
/// Die Domain-Typen werden direkt gemappt; die Domain bleibt frei von
/// EF-Abhängigkeiten.
///
/// Datenintegrität:
/// - AccountPostings: Primärschlüssel (Buchung, Konto) — eine Buchung
///   fasst dasselbe Konto maximal einmal auf (Regel auch in der Domain).
/// - PoolPostings: Primärschlüssel (Buchung, Pool).
/// - TaxPresets: Primärschlüssel KategorieId — 1:1 zu Kategorien.
/// - Foreign Keys auf Konten/Pools/Buchungen mit Restrict: historische
///   Referenzen überleben die Archivierung; Löschungen sind in Version 1
///   ohnehin ausgeschlossen.
/// </summary>
public class LarioDbContext : DbContext
{
    public LarioDbContext(DbContextOptions<LarioDbContext> options)
        : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<TaxCategory> TaxCategories => Set<TaxCategory>();
    public DbSet<TaxPreset> TaxPresets => Set<TaxPreset>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<AccountPosting> AccountPostings => Set<AccountPosting>();
    public DbSet<PoolPosting> PoolPostings => Set<PoolPosting>();
    public DbSet<ExpenseSplit> ExpenseSplits => Set<ExpenseSplit>();
    public DbSet<TaxMarking> SplitTaxMarkings => Set<TaxMarking>();
    public DbSet<PoolAllocation> TransferPurposes => Set<PoolAllocation>();
    public DbSet<AccountAllocation> ReassignmentAccounts => Set<AccountAllocation>();
    public DbSet<SettlementPosition> SettlementPositions => Set<SettlementPosition>();
    public DbSet<ReversalDeltaRow> ReversalDeltas => Set<ReversalDeltaRow>();
    public DbSet<BookingCorrection> BookingCorrections => Set<BookingCorrection>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.IsInterestBearing).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.BaseRevision).IsRequired();
        });

        modelBuilder.Entity<Pool>(e =>
        {
            e.ToTable("Pools");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.BaseRevision).IsRequired();
        });

        modelBuilder.Entity<Category>(e =>
        {
            e.ToTable("Categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ParentId).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.BaseRevision).IsRequired();
        });

        modelBuilder.Entity<TaxCategory>(e =>
        {
            e.ToTable("TaxCategories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ParentId).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.BaseRevision).IsRequired();
        });

        // Steuer-Voreinstellung: 1:1 zu einer Kategorie (PK = CategoryId).
        // TaxPreset ist die abhängige Seite; Category.TaxPreset ist die
        // Gegen-Navigation (keine zweite FK-Spalte auf Categories).
        // TaxCategoryId ist in Version 1 eine einfache Spalte; die
        // Referenzgültigkeit prüft die Domain (Katalog).
        modelBuilder.Entity<TaxPreset>(e =>
        {
            e.ToTable("TaxPresets");
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.CategoryId).HasMaxLength(128);
            e.Property(x => x.TaxRelevant);
            e.Property(x => x.TaxCategoryId).HasMaxLength(128);
            e.Property(x => x.InheritToChildren).IsRequired();
            e.Property(x => x.BaseRevision).IsRequired();
            e.HasOne<Category>()
                .WithOne(c => c.TaxPreset)
                .HasForeignKey<TaxPreset>(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.ToTable("Bookings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Date).IsRequired();
            e.Property(x => x.Note).HasMaxLength(4000);
            // Optimistische Synchronisation (Review R1 P1): die
            // Basisrevision ist ein Concurrency-Token — jede Änderung
            // (Korrektur, Storno, Erstattung) erhöht sie in derselben
            // Transaktion und SQLite prüft den erwarteten Wert im
            // UPDATE. Zwei parallele Operationen auf derselben Buchung
            // können nicht beide commiten; die Verliererin wird
            // vollständig zurückgerollt (keine halben Buchungen).
            e.Property(x => x.BaseRevision).IsRequired().IsConcurrencyToken();
            // Finanzierungsmonat nur bei Einnahmen (null sonst);
            // ISO-Monatsschlüssel "YYYY-MM".
            e.Property(x => x.FundingMonth)
                .HasConversion(
                    v => v.HasValue ? v.Value.ToString() : null,
                    v => string.IsNullOrEmpty(v) ? (YearMonth?)null : YearMonth.Parse(v))
                .HasMaxLength(7);
            // Begleichungsreferenz nur bei Kontotransfers (null sonst).
            e.Property(x => x.RelatedSettlementId).HasMaxLength(128);
            // Herkunft der Verteilungszeilen (true = explizit, false =
            // implizit, null = Buchungstyp ohne Verteilung) — Teil des
            // Original-Eingabeinhalts, für die Idempotenz der
            // Wiederholung gebraucht (Schritt-5-Review P2b).
            e.Property(x => x.AllocationExplicit);
            // Storno-Referenz (nur Reversal): stabile Id des Originals —
            // bewusst einfache Referenz ohne FK-Relation; die Existenz des
            // Originals prüft der Anwendungsfall (Design Schritt 6 §2.2).
            e.Property(x => x.ReversesBookingId).HasMaxLength(128);
            // Erstattungs-Referenz (nur Refund): stabile Id der
            // Ursprungs-Ausgabe (ebenfalls Referenz ohne FK).
            e.Property(x => x.RefundsExpenseId).HasMaxLength(128);
            // Gesichertes Anfangsbestandskonto (nur OpeningBalance, sonst
            // null): derselbe Wert wie das einzige Konto-Posting, zusätzlich
            // persistiert, damit die Einmaligkeitsregel „pro Konto genau ein
            // Anfangsbestand“ (Zwischenstep 7.5) datenbankseitig atomar
            // erzwungen werden kann. Partielles eindeutiges Index über
            // SQLite-Filter: die Spalte ist für alle anderen Buchungstypen
            // NULL, und NULL-Werte sind in SQLite-Indizes nicht eindeutig
            // (nur NULL == NULL wird ignoriert) — die Regel greift also
            // nur für Anfangsbestände.
            e.Property(x => x.OpeningBalanceAccountId).HasMaxLength(128);
            e.HasIndex(x => x.OpeningBalanceAccountId)
                .IsUnique()
                .HasFilter("OpeningBalanceAccountId IS NOT NULL")
                .HasDatabaseName("UX_Bookings_OpeningBalanceAccountId");
            // Die negierten Deltas liegen als eigene Zeilen in
            // ReversalDeltas; die Domain-Liste (record) mappen weder EF
            // noch das Modell — das Repository hält sie synchron.
            e.Ignore(x => x.ReversalDeltas);
        });

        modelBuilder.Entity<AccountPosting>(e =>
        {
            e.ToTable("AccountPostings");
            // Eine Buchung fasst dasselbe Konto maximal einmal auf. Die
            // Buchungszugehörigkeit ist eine Schatten-Fremdschlüssel-Spalte
            // (BookingId) aus der Sammlungsbeziehung — die Domain-Typen
            // bleiben frei von Persistenzdetails.
            e.HasKey("BookingId", "AccountId");
            e.Property(x => x.AccountId).HasMaxLength(128);
            e.Property(x => x.Amount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.HasOne<Booking>()
                .WithMany(b => b.AccountPostings)
                .HasForeignKey("BookingId")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PoolPosting>(e =>
        {
            e.ToTable("PoolPostings");
            // Eine Buchung fasst denselben Pool maximal einmal auf (siehe
            // AccountPostings: Schatten-Fremdschlüssel BookingId).
            e.HasKey("BookingId", "PoolId");
            e.Property(x => x.PoolId).HasMaxLength(128);
            e.Property(x => x.Amount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.HasOne<Booking>()
                .WithMany(b => b.PoolPostings)
                .HasForeignKey("BookingId")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Pool>()
                .WithMany()
                .HasForeignKey(x => x.PoolId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Teilposten einer Ausgabe (Schritt 4): Primärschlüssel
        // (Buchung, Teilposten-Id) — die Teilposten-Id ist innerhalb der
        // Buchung eindeutig (Regel auch in der Domain), global wird die
        // Buchungszugehörigkeit mitgeschaltet.
        modelBuilder.Entity<ExpenseSplit>(e =>
        {
            e.ToTable("ExpenseSplits");
            e.HasKey("BookingId", "Id");
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Amount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.Property(x => x.PoolId).HasMaxLength(128);
            e.Property(x => x.CategoryId).HasMaxLength(128);
            e.Property(x => x.Note).HasMaxLength(4000);
            e.HasOne<Booking>()
                .WithMany(b => b.Splits)
                .HasForeignKey("BookingId")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Pool>()
                .WithMany()
                .HasForeignKey(x => x.PoolId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>()
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Zweckverteilung eines Kontotransfers (Schritt 5): eine Buchung
        // fasst denselben Pool maximal einmal (Regel auch in der Domain).
        modelBuilder.Entity<PoolAllocation>(e =>
        {
            e.ToTable("TransferPurposes");
            e.HasKey("BookingId", "PoolId");
            e.Property(x => x.PoolId).HasMaxLength(128);
            e.Property(x => x.Amount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.HasOne<Booking>()
                .WithMany(b => b.TransferPurposes)
                .HasForeignKey("BookingId")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Pool>()
                .WithMany()
                .HasForeignKey(x => x.PoolId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Kontoverteilung einer Umwidmung (Schritt 5): eine Buchung fasst
        // dasselbe Konto maximal einmal (Regel auch in der Domain).
        modelBuilder.Entity<AccountAllocation>(e =>
        {
            e.ToTable("ReassignmentAccounts");
            e.HasKey("BookingId", "AccountId");
            e.Property(x => x.AccountId).HasMaxLength(128);
            e.Property(x => x.Amount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.HasOne<Booking>()
                .WithMany(b => b.ReassignmentAccounts)
                .HasForeignKey("BookingId")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Interne Ausgleichspositionen (Schritt 5, Rechenvertrag §3.5):
        // persistente Ansicht des Journals mit stabiler eigener Id. Die
        // Episode (Konto, Pool, erstellende Buchung) ist eindeutig.
        modelBuilder.Entity<SettlementPosition>(e =>
        {
            e.ToTable("SettlementPositions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.SourceAccountId).HasMaxLength(128);
            e.Property(x => x.TargetPoolId).HasMaxLength(128);
            e.Property(x => x.CreatedByBookingId).HasMaxLength(128);
            e.Property(x => x.SettledByBookingId).HasMaxLength(128);
            e.Property(x => x.Amount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.Property(x => x.RestAmount).HasConversion(v => v.Cents, v => new Money(v)).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.CreatedDate).IsRequired();
            e.Property(x => x.Note).HasMaxLength(4000);
            e.HasOne<Account>()
                .WithMany()
                .HasForeignKey(x => x.SourceAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Pool>()
                .WithMany()
                .HasForeignKey(x => x.TargetPoolId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Booking>()
                .WithMany()
                .HasForeignKey(x => x.CreatedByBookingId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Booking>()
                .WithMany()
                .HasForeignKey(x => x.SettledByBookingId)
                .OnDelete(DeleteBehavior.Restrict);
            // Eine Zelle trägt zu einem Zeitpunkt maximal eine Episode
            // pro erstellender Buchung; die Episode ist identisch.
            e.HasIndex(x => new { x.SourceAccountId, x.TargetPoolId, x.CreatedByBookingId })
                .IsUnique();
        });

        // Steuerliche Kennzeichnung eines Teilpostens (Schritt 4): 1:1 zu
        // einem Teilposten (optional — ein Teilposten kann unmarkiert
        // sein). Die Kennzeichnung verändert keine Finanzsalden.
        modelBuilder.Entity<TaxMarking>(e =>
        {
            e.ToTable("SplitTaxMarkings");
            // Schatten-Properties zuerst deklarieren (Typ!), dann als
            // Schlüssel/FK verwenden. Der Komposit-Schlüssel (SplitId,
            // BookingId) deckt beide FK-Spalten ab und ist damit korrekt
            // 1:1 zu ExpenseSplit (Komposit-Key (BookingId, Id)).
            e.Property<string>("SplitId").HasMaxLength(128);
            e.Property<string>("BookingId").HasMaxLength(128);
            e.HasKey(new[] { "SplitId", "BookingId" });
            e.Property(x => x.Relevant).IsRequired();
            // Herkunft der Kennzeichnung (explizit / aus Voreinstellung
            // materialisiert) — Teil des Original-Eingabeinhalts, für die
            // Idempotenz der Wiederholung gebraucht (Schritt-4-Review P2b).
            e.Property(x => x.InheritedFromPreset).IsRequired();
            e.Property(x => x.TaxCategoryId).HasMaxLength(128);
            e.Property(x => x.MarkedAmount)
                .HasConversion(
                    (Money? v) => v.HasValue ? v.Value.Cents : (long?)null,
                    (long? v) => v.HasValue ? new Money(v.Value) : null)
                .IsRequired(false);
            e.Property(x => x.Note).HasMaxLength(4000);
            e.HasOne<ExpenseSplit>()
                .WithOne(s => s.Tax)
                // Reihenfolge muss den Principal-Key (BookingId, Id) von
                // ExpenseSplit positionsmäßig treffen: Spalte BookingId ->
                // Principal-BookingId, Spalte SplitId -> Principal-Id.
                .HasForeignKey<TaxMarking>(new[] { "BookingId", "SplitId" })
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Negierte Deltas eines Stornos (Design Schritt 6 §2.2): die
        // explizit gespeicherte Gegenbewegung zum Original — im Replay
        // kein Cross-Booking-Lookup. Primärschlüssel (Buchung, Konto,
        // Pool): ein Storno fasst dieselbe Zelle nicht doppelt auf
        // (Invariante auch in der Domain).
        modelBuilder.Entity<ReversalDeltaRow>(e =>
        {
            e.ToTable("ReversalDeltas");
            e.HasKey(x => new { x.BookingId, x.AccountId, x.PoolId });
            e.Property(x => x.BookingId).HasMaxLength(128);
            e.Property(x => x.AccountId).HasMaxLength(128);
            e.Property(x => x.PoolId).HasMaxLength(128);
            e.Property(x => x.Amount).IsRequired();
            e.HasOne<Booking>()
                .WithMany()
                .HasForeignKey(x => x.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Beleg-Metadaten (Schritt 7a, Design D1): die Datei selbst liegt
        // außerhalb der Datenbank (Ablageverzeichnis, serverseitiger
        // Name); hier nur Metadaten + Zuordnung. Die Buchungszugehörigkeit
        // ist erforderlich (Restrict — Buchungen werden in Version 1
        // nicht gelöscht); der Split-Bezug ist ein Komposit-FK
        // (BookingId, SplitId) auf ExpenseSplits, weil die Teilposten-Id
        // nur innerhalb der Buchung eindeutig ist (Muster: SplitTaxMarkings
        // nutzt denselben Komposit-Schlüssel). OnDelete(Restrict) an beiden
        // Stellen: unbeabsichtigtes Löschen von Buchung/Split ohne
        // Umlageung (Design D1b) schlägt laut fehl statt dass Metadaten
        // verschwinden und die Datei zurückbleibt. OriginalSplitId ist
        // bewusst eine einfache Spalte ohne FK — sie referenziert
        // gelöschte Splits (Nachvollziehbarkeit der Umlageung).
        modelBuilder.Entity<Attachment>(e =>
        {
            e.ToTable("Attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.OriginalFilename).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(64).IsRequired();
            e.Property(x => x.SizeBytes).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(255).IsRequired();
            e.Property(x => x.BookingId).HasMaxLength(128).IsRequired();
            e.Property(x => x.SplitId).HasMaxLength(128);
            e.Property(x => x.OriginalSplitId).HasMaxLength(128);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            // Null bis zur ersten manuellen Umzuordnung (Nachtrag N1).
            e.Property(x => x.UpdatedAtUtc);
            e.HasOne<Booking>()
                .WithMany()
                .HasForeignKey(x => x.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
            // Reihenfolge muss den Principal-Key (BookingId, Id) von
            // ExpenseSplit positionsmäßig treffen: Spalte BookingId ->
            // Principal-BookingId, Spalte SplitId -> Principal-Id.
            // null SplitId = Beleg für die gesamte Ausgabe.
            e.HasOne<ExpenseSplit>()
                .WithMany()
                .HasForeignKey(new[] { "BookingId", "SplitId" })
                .OnDelete(DeleteBehavior.Restrict);
            // Ein Dateiname darf höchstens eine Metadatenzeile tragen
            // (StoredName wird aus der eindeutigen Id abgeleitet; der
            // Index ist defensiv und unterstützt den Start-Abgleich).
            e.HasIndex(x => x.StoredName)
                .IsUnique()
                .HasDatabaseName("UX_Attachments_StoredName");
        });

        // Korrektur-Historie (Design Schritt 6 §2.4/§2.5): Vorher/
        // Nachher-Snapshot je In-place-Änderung; OperationId ist der
        // Idempotenzschlüssel (Rechenvertrag §5.5). Solange Einträge die
        // Buchung referenzieren, bleibt sie unverzichtbar (Restrict).
        modelBuilder.Entity<BookingCorrection>(e =>
        {
            e.ToTable("BookingCorrections");
            e.HasKey(x => x.OperationId);
            e.Property(x => x.OperationId).HasMaxLength(128);
            e.Property(x => x.BookingId).HasMaxLength(128);
            // JSON-Snapshots: TEXT ohne Max-Length.
            e.Property(x => x.OldSnapshot).IsRequired();
            e.Property(x => x.NewSnapshot).IsRequired();
            // Kanonischer Original-Payload (Idempotenznachweis, Review
            // R1 P2): JSON-TEXT ohne Max-Length.
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.Note).HasMaxLength(4000);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.HasOne<Booking>()
                .WithMany()
                .HasForeignKey(x => x.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
