using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LiveTrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "liquidation_price",
                table: "trading_positions",
                type: "numeric(28,10)",
                precision: 28,
                scale: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "live_equity_usd",
                table: "trading_accounts",
                type: "numeric(20,2)",
                precision: 20,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "live_sync_error",
                table: "trading_accounts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "live_synced_at",
                table: "trading_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sent_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "venue_client_id",
                table: "orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "venue_order_id",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
            // "xmin" is Postgres' own change counter and is on every table already: there is nothing to add.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "liquidation_price",
                table: "trading_positions");

            migrationBuilder.DropColumn(
                name: "live_equity_usd",
                table: "trading_accounts");

            migrationBuilder.DropColumn(
                name: "live_sync_error",
                table: "trading_accounts");

            migrationBuilder.DropColumn(
                name: "live_synced_at",
                table: "trading_accounts");

            migrationBuilder.DropColumn(
                name: "sent_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "venue_client_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "venue_order_id",
                table: "orders");
        }
    }
}
