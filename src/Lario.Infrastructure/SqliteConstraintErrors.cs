using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// Erkennung von SQLite-FK-Verletzungen in EF-Update-Ausnahmen
/// (Schritt 7a, Review Befund 3): Ein <see cref="DbUpdateException"/>
/// ist genau dann eine "FOREIGN KEY constraint failed", wenn die
/// innere <see cref="SqliteException"/> den erweiterten Fehlercode
/// SQLITE_CONSTRAINT_FOREIGNKEY trägt. Die Codes sind empirisch
/// verifiziert (Microsoft.Data.Sqlite 10.0.12): Basiscode 19
/// (SQLITE_CONSTRAINT), erweiterter Code 787 = (19 | 3 &lt;&lt; 8).
/// Im Gegensatz zu den anderen Constraint-Untercodes (CHECK 275,
/// NOT NULL 1299, UNIQUE 2067) ist er eindeutig — eine String-Prüfung
/// des Fehlertextes wäre fehleranfällig (Locale, EF-Zusatztext).
/// </summary>
internal static class SqliteConstraintErrors
{
    /// <summary>SQLITE_CONSTRAINT_FOREIGNKEY (erweiterter Fehlercode).</summary>
    private const int ForeignKeyExtendedCode = 787;

    /// <summary>
    /// True, wenn <paramref name="ex"/> auf eine SQLite-FK-Verletzung
    /// zurückzuführen ist; false für alle anderen Update-Fehler
    /// (einschließlich Revisionskonflikte und technischer Fehler).
    /// </summary>
    public static bool IsForeignKeyViolation(DbUpdateException ex)
        => ex.InnerException is SqliteException sql
           && sql.SqliteExtendedErrorCode == ForeignKeyExtendedCode;
}
