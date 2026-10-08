using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "summarized_until",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "conversations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summarized_until",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "summary",
                table: "conversations");
        }
    }
}
