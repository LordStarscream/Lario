using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step9SyncProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_changes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DbGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_operations",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    BookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_operations", x => new { x.DeviceId, x.OperationId });
                });

            migrationBuilder.CreateTable(
                name: "sync_protocol_state",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DbGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ProtocolGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    CursorCounter = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_protocol_state", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_devices_TokenHash",
                table: "sync_devices",
                column: "TokenHash",
                unique: true);

            // Protokollzustand von Anfang an speichern (eine Zeile für die
            // gesamte Datenbank-Generation; Restore erzeugt eine neue
            // Generation mit neuem Seed — Batch B):
            // DbGeneration 1, ProtocolGeneration 1, CursorCounter 0.
            migrationBuilder.InsertData(
                table: "sync_protocol_state",
                columns: new[] { "Id", "DbGeneration", "ProtocolGeneration", "CursorCounter" },
                values: new object[] { "1", 1L, 1L, 0L });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "sync_protocol_state",
                keyColumn: "Id",
                keyValue: "1");

            migrationBuilder.DropTable(
                name: "sync_changes");

            migrationBuilder.DropTable(
                name: "sync_devices");

            migrationBuilder.DropTable(
                name: "sync_operations");

            migrationBuilder.DropTable(
                name: "sync_protocol_state");
        }
    }
}
