using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IncomeExpenseSplits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FundingMonth",
                table: "Bookings",
                type: "TEXT",
                maxLength: 7,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseSplits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BookingId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    PoolId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CategoryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSplits", x => new { x.BookingId, x.Id });
                    table.ForeignKey(
                        name: "FK_ExpenseSplits_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExpenseSplits_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseSplits_Pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SplitTaxMarkings",
                columns: table => new
                {
                    SplitId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Relevant = table.Column<bool>(type: "INTEGER", nullable: false),
                    TaxCategoryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MarkedAmount = table.Column<long>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    BookingId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SplitTaxMarkings", x => x.SplitId);
                    table.ForeignKey(
                        name: "FK_SplitTaxMarkings_ExpenseSplits_SplitId_BookingId",
                        columns: x => new { x.SplitId, x.BookingId },
                        principalTable: "ExpenseSplits",
                        principalColumns: new[] { "BookingId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSplits_CategoryId",
                table: "ExpenseSplits",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSplits_PoolId",
                table: "ExpenseSplits",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_SplitTaxMarkings_SplitId_BookingId",
                table: "SplitTaxMarkings",
                columns: new[] { "SplitId", "BookingId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SplitTaxMarkings");

            migrationBuilder.DropTable(
                name: "ExpenseSplits");

            migrationBuilder.DropColumn(
                name: "FundingMonth",
                table: "Bookings");
        }
    }
}
