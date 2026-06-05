using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Biletix.Api.Migrations
{
    /// <inheritdoc />
    public partial class TicketReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReservedBy",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReservedUntil",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_Status_ReservedUntil",
                table: "tickets",
                columns: new[] { "Status", "ReservedUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_Status_ReservedUntil",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ReservedBy",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ReservedUntil",
                table: "tickets");
        }
    }
}
