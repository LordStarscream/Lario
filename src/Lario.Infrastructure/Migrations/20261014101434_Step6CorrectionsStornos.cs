using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step6CorrectionsStornos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefundsExpenseId",
                table: "Bookings",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversesBookingId",
                table: "Bookings",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingCorrections",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OldSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    NewSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCorrections", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_BookingCorrections_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReversalDeltas",
                columns: table => new
                {
                    BookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PoolId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReversalDeltas", x => new { x.BookingId, x.AccountId, x.PoolId });
                    table.ForeignKey(
                        name: "FK_ReversalDeltas_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCorrections_BookingId",
                table: "BookingCorrections",
                column: "BookingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCorrections");

            migrationBuilder.DropTable(
                name: "ReversalDeltas");

            migrationBuilder.DropColumn(
                name: "RefundsExpenseId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ReversesBookingId",
                table: "Bookings");
        }
    }
}
