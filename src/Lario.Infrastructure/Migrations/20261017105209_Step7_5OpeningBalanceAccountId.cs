using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step7_5OpeningBalanceAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpeningBalanceAccountId",
                table: "Bookings",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            // Backfill für Bestandsdatenbanken (Anfangsbestände, die vor der
            // Spalte existieren): die gesicherte Konto-Id wird aus dem
            // (einzigen) Konto-Posting übernommen. Einmaligkeitsregel „pro
            // Konto genau einer": die älteste Legacy-Zeile pro Konto
            // (Fachdatum, dann Id) bekommt das Konto; weitere Zeilen desselben
            // Kontos (Legacy-Duplikate) bleiben NULL — der partielle Unique-
            // Index und die serviceseitige posting-basierte Prüfung halten sie
            // sichtbar und blockieren Neuanlagen. Nur Zeilen für Konten, die
            // danach noch frei sind, werden nachgefüllt, damit die Aussage
            // „eine Buchung je Konto“ auch bei bereits vorhandenem Index
            // niemals verletzt wird. Beide Statements sind idempotent.
            migrationBuilder.Sql(
                @"UPDATE Bookings
SET OpeningBalanceAccountId = (
        SELECT ap.AccountId
        FROM AccountPostings ap
        WHERE ap.BookingId = Bookings.Id
        ORDER BY ap.AccountId
        LIMIT 1)
WHERE Type = 'OpeningBalance'
  AND OpeningBalanceAccountId IS NULL
  AND Id IN (
        SELECT b2.Id
        FROM Bookings b2
        INNER JOIN AccountPostings ap2 ON ap2.BookingId = b2.Id
        WHERE b2.Type = 'OpeningBalance'
          AND b2.OpeningBalanceAccountId IS NULL
          AND NOT EXISTS (
                SELECT 1
                FROM Bookings b3
                INNER JOIN AccountPostings ap3 ON ap3.BookingId = b3.Id
                WHERE b3.Type = 'OpeningBalance'
                  AND b3.OpeningBalanceAccountId IS NULL
                  AND ap3.AccountId = ap2.AccountId
                  AND (b3.Date < b2.Date
                       OR (b3.Date = b2.Date AND b3.Id < b2.Id))));");

            migrationBuilder.Sql(
                @"UPDATE Bookings
SET OpeningBalanceAccountId = (
        SELECT ap.AccountId
        FROM AccountPostings ap
        WHERE ap.BookingId = Bookings.Id
        ORDER BY ap.AccountId
        LIMIT 1)
WHERE Type = 'OpeningBalance'
  AND OpeningBalanceAccountId IS NULL
  AND NOT EXISTS (
        SELECT 1
        FROM Bookings b3
        WHERE b3.Type = 'OpeningBalance'
          AND b3.OpeningBalanceAccountId IS NOT NULL
          AND b3.OpeningBalanceAccountId = (
                SELECT ap4.AccountId
                FROM AccountPostings ap4
                WHERE ap4.BookingId = Bookings.Id
                ORDER BY ap4.AccountId
                LIMIT 1));");

            migrationBuilder.CreateIndex(
                name: "UX_Bookings_OpeningBalanceAccountId",
                table: "Bookings",
                column: "OpeningBalanceAccountId",
                unique: true,
                filter: "OpeningBalanceAccountId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Bookings_OpeningBalanceAccountId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OpeningBalanceAccountId",
                table: "Bookings");
        }
    }
}
