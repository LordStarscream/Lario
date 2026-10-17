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
