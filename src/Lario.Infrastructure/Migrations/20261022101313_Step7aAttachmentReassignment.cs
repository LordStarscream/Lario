using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lario.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step7aAttachmentReassignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Attachments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Attachments");
        }
    }
}
