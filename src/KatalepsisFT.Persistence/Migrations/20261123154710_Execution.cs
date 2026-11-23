using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Execution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "funds",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "owner_name",
                table: "funds",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "approval_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    fund_id = table.Column<int>(type: "integer", nullable: true),
                    trading_account_id = table.Column<int>(type: "integer", nullable: true),
                    trading_halt_id = table.Column<int>(type: "integer", nullable: true),
                    summary = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_ids = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    max_order_notional_usd = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trading_accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manager_account_id = table.Column<int>(type: "integer", nullable: false),
                    venue = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    encrypted_key = table.Column<string>(type: "text", nullable: true),
                    key_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    leverage = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    margin_mode = table.Column<int>(type: "integer", nullable: false),
                    max_order_notional_usd = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    max_position_notional_usd = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    price_band_bps = table.Column<int>(type: "integer", nullable: false),
                    paper_balance_usd = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trading_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_trading_accounts_manager_accounts_manager_account_id",
                        column: x => x.manager_account_id,
                        principalTable: "manager_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "trading_halts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    venue = table.Column<int>(type: "integer", nullable: true),
                    trading_account_id = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    pulled_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    pulled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trading_halts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    client_order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trading_account_id = table.Column<int>(type: "integer", nullable: false),
                    asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    instrument_type = table.Column<int>(type: "integer", nullable: false),
                    side = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    time_in_force = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    limit_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    reduce_only = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    filled_quantity = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    average_fill_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    fees_usd = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    arrival_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    reject_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    submitted_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    strategy_key_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_orders_trading_accounts_trading_account_id",
                        column: x => x.trading_account_id,
                        principalTable: "trading_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "trading_positions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trading_account_id = table.Column<int>(type: "integer", nullable: false),
                    asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    instrument_type = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    average_price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    realised_pnl_usd = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    fees_usd = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trading_positions", x => x.id);
                    table.ForeignKey(
                        name: "fk_trading_positions_trading_accounts_trading_account_id",
                        column: x => x.trading_account_id,
                        principalTable: "trading_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    venue = table.Column<int>(type: "integer", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    price = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    fee_usd = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_events_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_status",
                table: "approval_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_order_events_order_id",
                table: "order_events",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_created_at",
                table: "orders",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status",
                table: "orders",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_orders_trading_account_id_client_order_id",
                table: "orders",
                columns: new[] { "trading_account_id", "client_order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_strategy_keys_key_hash",
                table: "strategy_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_settings_name",
                table: "system_settings",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trading_accounts_manager_account_id_venue",
                table: "trading_accounts",
                columns: new[] { "manager_account_id", "venue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trading_halts_released_at",
                table: "trading_halts",
                column: "released_at");

            migrationBuilder.CreateIndex(
                name: "ix_trading_positions_trading_account_id_asset_instrument_type",
                table: "trading_positions",
                columns: new[] { "trading_account_id", "asset", "instrument_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_requests");

            migrationBuilder.DropTable(
                name: "order_events");

            migrationBuilder.DropTable(
                name: "strategy_keys");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "trading_halts");

            migrationBuilder.DropTable(
                name: "trading_positions");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "trading_accounts");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "funds");

            migrationBuilder.DropColumn(
                name: "owner_name",
                table: "funds");
        }
    }
}
