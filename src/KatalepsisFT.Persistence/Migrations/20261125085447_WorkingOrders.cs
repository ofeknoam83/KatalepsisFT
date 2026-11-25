using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkingOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "working_order_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "working_orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manager_account_id = table.Column<int>(type: "integer", nullable: false),
                    trading_account_id = table.Column<int>(type: "integer", nullable: true),
                    asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    instrument_type = table.Column<int>(type: "integer", nullable: false),
                    side = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    limit_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    reduce_only = table.Column<bool>(type: "boolean", nullable: false),
                    style = table.Column<int>(type: "integer", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    slices = table.Column<int>(type: "integer", nullable: true),
                    min_slice = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    max_slice = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    wait_seconds = table.Column<int>(type: "integer", nullable: true),
                    pause_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    max_slippage_bps = table.Column<int>(type: "integer", nullable: true),
                    slippage_accepted = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    stop_requested = table.Column<bool>(type: "boolean", nullable: false),
                    filled_quantity = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    average_fill_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    fees_usd = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    arrival_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    slices_sent = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_slice_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hard_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_working_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_working_orders_manager_accounts_manager_account_id",
                        column: x => x.manager_account_id,
                        principalTable: "manager_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_working_orders_trading_accounts_trading_account_id",
                        column: x => x.trading_account_id,
                        principalTable: "trading_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_working_order_id",
                table: "orders",
                column: "working_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_working_orders_manager_account_id_client_id",
                table: "working_orders",
                columns: new[] { "manager_account_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_working_orders_state",
                table: "working_orders",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_working_orders_trading_account_id",
                table: "working_orders",
                column: "trading_account_id");

            migrationBuilder.AddForeignKey(
                name: "fk_orders_working_orders_working_order_id",
                table: "orders",
                column: "working_order_id",
                principalTable: "working_orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_orders_working_orders_working_order_id",
                table: "orders");

            migrationBuilder.DropTable(
                name: "working_orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_working_order_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "working_order_id",
                table: "orders");
        }
    }
}
