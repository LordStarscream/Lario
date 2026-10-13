using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TransfersAndSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RelatedSettlementId",
                table: "Bookings",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReassignmentAccounts",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BookingId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReassignmentAccounts", x => new { x.BookingId, x.AccountId });
                    table.ForeignKey(
                        name: "FK_ReassignmentAccounts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassignmentAccounts_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettlementPositions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TargetPoolId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    RestAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedByBookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SettledByBookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SettledDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementPositions_Accounts_SourceAccountId",
                        column: x => x.SourceAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementPositions_Bookings_CreatedByBookingId",
                        column: x => x.CreatedByBookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementPositions_Bookings_SettledByBookingId",
                        column: x => x.SettledByBookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementPositions_Pools_TargetPoolId",
                        column: x => x.TargetPoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransferPurposes",
                columns: table => new
                {
                    PoolId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BookingId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferPurposes", x => new { x.BookingId, x.PoolId });
                    table.ForeignKey(
                        name: "FK_TransferPurposes_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransferPurposes_Pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReassignmentAccounts_AccountId",
                table: "ReassignmentAccounts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementPositions_CreatedByBookingId",
                table: "SettlementPositions",
                column: "CreatedByBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementPositions_SettledByBookingId",
                table: "SettlementPositions",
                column: "SettledByBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementPositions_SourceAccountId_TargetPoolId_CreatedByBookingId",
                table: "SettlementPositions",
                columns: new[] { "SourceAccountId", "TargetPoolId", "CreatedByBookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SettlementPositions_TargetPoolId",
                table: "SettlementPositions",
                column: "TargetPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferPurposes_PoolId",
                table: "TransferPurposes",
                column: "PoolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReassignmentAccounts");

            migrationBuilder.DropTable(
                name: "SettlementPositions");

            migrationBuilder.DropTable(
                name: "TransferPurposes");

            migrationBuilder.DropColumn(
                name: "RelatedSettlementId",
                table: "Bookings");
        }
    }
}
