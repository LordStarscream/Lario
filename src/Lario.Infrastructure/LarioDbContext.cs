using Lario.Domain;
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
            e.Property(x => x.BaseRevision).IsRequired();
            // Finanzierungsmonat nur bei Einnahmen (null sonst);
            // ISO-Monatsschlüssel "YYYY-MM".
            e.Property(x => x.FundingMonth)
                .HasConversion(
                    v => v.HasValue ? v.Value.ToString() : null,
                    v => string.IsNullOrEmpty(v) ? (YearMonth?)null : YearMonth.Parse(v))
                .HasMaxLength(7);
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
    }
}
