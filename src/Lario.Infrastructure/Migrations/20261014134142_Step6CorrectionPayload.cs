using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step6CorrectionPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "BookingCorrections",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Payload",
                table: "BookingCorrections");
        }
    }
}
