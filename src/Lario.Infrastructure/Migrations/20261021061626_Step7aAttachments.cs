using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step7aAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OriginalFilename = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    BookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SplitId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OriginalSplitId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Attachments_ExpenseSplits_BookingId_SplitId",
                        columns: x => new { x.BookingId, x.SplitId },
                        principalTable: "ExpenseSplits",
                        principalColumns: new[] { "BookingId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_BookingId_SplitId",
                table: "Attachments",
                columns: new[] { "BookingId", "SplitId" });

            migrationBuilder.CreateIndex(
                name: "UX_Attachments_StoredName",
                table: "Attachments",
                column: "StoredName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
