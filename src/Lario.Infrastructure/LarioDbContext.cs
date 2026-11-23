using Lario.Domain;
using Lario.Domain.Attachments;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Changelog des Sync-Protokolls (Schritt 9): Jede erstellte oder
    /// geänderte Buchungszeile erzeugt in derselben Transaktion genau
    /// eine Änderungszeile — unabhängig vom Schreibpfad (Session-API,
    /// Sync-Push, Korrekturen, Storno). Stammdaten-Änderungen trägt der
    /// explizite Haken in <c>MasterDataRepository</c> ein (Design D3);
    /// diese Zeilen zählt der Kontext nur für den Cursor mit. So entsteht
    /// pro Änderung genau eine Zeile, ohne dass die Haken dupliziert
    /// werden.
    /// </summary>
    public override int SaveChanges()
    {
        var neueZeilen = CollectSyncChanges();
        var result = base.SaveChanges();
        UpdateCursorCounter(neueZeilen);
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var neueZeilen = CollectSyncChanges();
        return SaveChangesAsyncCore(neueZeilen, cancellationToken);
    }

    private async Task<int> SaveChangesAsyncCore(
        IReadOnlyList<SyncChange> neueZeilen, CancellationToken cancellationToken)
    {
        var result = await base.SaveChangesAsync(cancellationToken);
        await UpdateCursorCounterAsync(neueZeilen, cancellationToken);
        return result;
    }

    /// <summary>
    /// Cursor (Design D2): <c>SyncProtocolState.CursorCounter</c> spiegelt
    /// die größte Changelog-Id. Die Zeilen haben ihre Ids erst nach dem
    /// eigentlichen Save; der kurze Folgesave läuft über diesen Override
    /// erneut — da dann keine Buchungsänderung mehr offen ist, fügt
    /// <see cref="CollectSyncChanges"/> nichts hinzu (keine Rekursion).
    /// </summary>
    private void UpdateCursorCounter(IReadOnlyList<SyncChange> neueZeilen)
    {
        if (neueZeilen.Count == 0)
            return;

        var maxId = neueZeilen.Max(c => c.Id);
        var state = SyncProtocolStates.FirstOrDefault(s => s.Id == "1");
        if (state is not null && state.CursorCounter < maxId)
        {
            state.CursorCounter = maxId;
            SaveChanges();
        }
    }

    private async Task UpdateCursorCounterAsync(
        IReadOnlyList<SyncChange> neueZeilen, CancellationToken cancellationToken)
    {
        if (neueZeilen.Count == 0)
            return;

        var maxId = neueZeilen.Max(c => c.Id);
        var state = await SyncProtocolStates.FirstOrDefaultAsync(s => s.Id == "1", cancellationToken);
        if (state is not null && state.CursorCounter < maxId)
        {
            state.CursorCounter = maxId;
            await SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Einmaliger, gecachter Check: existiert die Changelog-Tabelle?
    /// Auf Legacy-Datenbanken (vor Schritt 9) nicht — der Upgrade-Pfad
    /// arbeitet dort ohne Sync-Tabellen.
    /// </summary>
    private bool? _syncChangelogAvailable;

    private bool SyncChangelogAvailable
    {
        get
        {
            if (_syncChangelogAvailable is null)
            {
                try
                {
                    // Skalar-Abfrage von SqlQuery<T>: EF erwartet eine
                    // Ergebnisspalte mit dem Namen "Value" (Wrapper
                    // "SELECT s.Value FROM (…) AS s"); ohne Alias wirft die
                    // Abfrage eine SqliteException, die diesen Check
                    // stillschweigend deaktivieren würde.
                    _syncChangelogAvailable = Database
                        .SqlQuery<int>(
                            $"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'sync_changes'")
                        .FirstOrDefault() > 0;
                }
                catch (SqliteException)
                {
                    // Verbindungs-/Zugriffsproblem: konservativ kein
                    // Changelog schreiben (die eigentliche SaveChanges
                    // meldet dann den Fehler).
                    _syncChangelogAvailable = false;
                }
            }

            return _syncChangelogAvailable.Value;
        }
    }

    /// <summary>
    /// Sammelt die Changelog-Zeilen dieses Saves:
    /// <list type="bullet">
    /// <item>Neue/geänderte/gelöschte <see cref="Booking"/>-Entitäten →
    /// je eine Zeile (Design D6: jede Buchungszeile ist eine
    /// Changelog-Zeile, egal welcher Pfad schreibt — Session-API, Push,
    /// Korrektur).</item>
    /// <item>Zusätzlich werden die Changelog-Zeilen gezählt, die externe
    /// Haken (Design D3: <c>MasterDataRepository</c>) bereits in den
    /// Tracker gestellt haben — sie gehören zum selben Save und zählen
    /// für den Cursor, werden aber hier nicht erneut angelegt.</item>
    /// </list>
    /// Stammdaten-Entitäten (Konto/Pool/Kategorie/Steuerkategorie/
    /// Voreinstellung) werden hier bewusst NICHT gesammelt: deren Haken
    /// liegen nach D3 in den <c>MasterDataRepository</c>-Schreibmethoden;
    /// eine zusätzliche Sammlung würde pro Änderung zwei Zeilen erzeugen.
    /// </summary>
    private IReadOnlyList<SyncChange> CollectSyncChanges()
    {
        // Materialisieren: die Entry-Liste ist eine Live-Ansicht und
        // würde während des Hinzufügens der Changelog-Zeilen modifiziert.
        var entries = ChangeTracker.Entries().ToList();

        // Zeilen, die ein externer Haken (D3) diesem Save schon zugewiesen
        // hat: sie zählen für den Cursor, auch wenn der Sammel-Teil unter
        // dem Strich nichts findet (Legacy-DB, reine Stammdaten-Änderung).
        var zeilen = entries
            .Where(e => e.State == EntityState.Added && e.Entity is SyncChange)
            .Select(e => (SyncChange)e.Entity!)
            .ToList();

        var buchungen = entries
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && e.Entity is Booking)
            .ToList();

        // Legacy-Datenbank (vor Schritt 9): die Sync-Tabellen existieren
        // noch nicht (A2-Upgrade-Pfad) — dann gehört kein Changelog dazu.
        if (buchungen.Count > 0 && !SyncChangelogAvailable)
        {
            // Buchungsänderung ohne Changelog-Tabelle: die Haken-Zeilen
            // würden auf eine nicht existierende Tabelle schreiben und
            // den Save brechen — das kann hier nur eine Legacy-DB sein,
            // auf der auch die Haken nicht laufen (Migration legt beide
            // zusammen an). Defensive Abfrage, in Version 1 nicht
            // erreichbar.
            return zeilen;
        }

        if (buchungen.Count == 0)
            return zeilen;

        // Datenbank-Generation der Zeile (Restore erzeugt eine neue —
        // Batch B); fehlender Zustand ist in Version 1 Generation 1.
        var dbGeneration = SyncProtocolStates.FirstOrDefault(s => s.Id == "1")
            ?.DbGeneration ?? 1;
        var now = DateTime.UtcNow;

        foreach (var entry in buchungen)
        {
            var booking = (Booking)entry.Entity!;
            var zeile = new SyncChange
            {
                Id = 0,
                DbGeneration = dbGeneration,
                EntityType = "booking",
                EntityId = booking.Id,
                Revision = booking.BaseRevision,
                OccurredAtUtc = now
            };
            SyncChanges.Add(zeile);
            zeilen.Add(zeile);
        }

        return zeilen;
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

    // Sync-Protokoll (Schritt 9, Batch A1): Gerätekopplung, Protokoll-
    // zustand, Changelog und Idempotenznachweise (Infrastruktur-Entitäten,
    // siehe SyncEntities.cs).
    public DbSet<SyncDevice> SyncDevices => Set<SyncDevice>();
    public DbSet<SyncProtocolState> SyncProtocolStates => Set<SyncProtocolState>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<SyncOperation> SyncOperations => Set<SyncOperation>();

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

        // ---------- Sync-Protokoll (Schritt 9, Batch A1) ----------

        // Gekoppeltes Gerät: das Klartext-Gerätetoken wird nicht
        // persistiert, nur dessen SHA-256-Hash (eindeutig — ein Token
        // gehört zu genau einem Gerät).
        modelBuilder.Entity<SyncDevice>(e =>
        {
            e.ToTable("sync_devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.RevokedAtUtc);
            e.Property(x => x.LastUsedAtUtc);
        });

        // Protokollzustand: genau eine Zeile (Id '1'); die Zeile wird
        // von der Migration Step9SyncProtocol angelegt und bleibt für
        // die gesamte Lebensdauer der Datenbank erhalten.
        modelBuilder.Entity<SyncProtocolState>(e =>
        {
            e.ToTable("sync_protocol_state");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(16);
            e.Property(x => x.DbGeneration).IsRequired();
            e.Property(x => x.ProtocolGeneration).IsRequired();
            e.Property(x => x.CursorCounter).IsRequired();
        });

        // Changelog-Zeilen: die long-Id ist INTEGER PRIMARY KEY
        // (rowid + AUTOINCREMENT) und damit der Cursorwert — monoton
        // wachsend, nie gelöscht, nie wiederverwendet.
        modelBuilder.Entity<SyncChange>(e =>
        {
            e.ToTable("sync_changes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.DbGeneration).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(32).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Revision).IsRequired();
            e.Property(x => x.OccurredAtUtc).IsRequired();
        });

        // Idempotenznachweis: Komposit-PK (DeviceId, OperationId) —
        // dieselbe Operation desselben Geräts erzeugt höchstens eine
        // Zeile; der Unique-Constraint entscheidet parallele
        // Doppel-Sendungen (einer gewinnt, der andere liest die
        // bestehende Zeile oder erhält bereits verarbeitete/mismatched).
        modelBuilder.Entity<SyncOperation>(e =>
        {
            e.ToTable("sync_operations");
            e.HasKey(x => new { x.DeviceId, x.OperationId });
            e.Property(x => x.DeviceId).HasMaxLength(128);
            e.Property(x => x.OperationId).HasMaxLength(128);
            e.Property(x => x.Type).HasMaxLength(32).IsRequired();
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            // JSON-Text ohne Max-Length (wie BookingCorrections.Payload).
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.BookingId).HasMaxLength(128).IsRequired();
            e.Property(x => x.AcceptedAtUtc).IsRequired();
        });
    }
}
