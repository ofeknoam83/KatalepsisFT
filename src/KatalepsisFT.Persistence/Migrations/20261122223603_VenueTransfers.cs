using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VenueTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "transfers_checked_at",
                table: "venue_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transfers_error",
                table: "venue_connections",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "cash_flows",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cash_flows_manager_account_id_external_id",
                table: "cash_flows",
                columns: new[] { "manager_account_id", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cash_flows_manager_account_id_external_id",
                table: "cash_flows");

            migrationBuilder.DropColumn(
                name: "transfers_checked_at",
                table: "venue_connections");

            migrationBuilder.DropColumn(
                name: "transfers_error",
                table: "venue_connections");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "cash_flows");
        }
    }
}
