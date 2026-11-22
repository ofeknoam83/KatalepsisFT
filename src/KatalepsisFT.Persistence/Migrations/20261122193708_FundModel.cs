using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FundModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asset_class = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "benchmarks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    is_approximation = table.Column<bool>(type: "boolean", nullable: false),
                    fixed_annual_rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_benchmarks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "instruments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    multiplier = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    strike = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    option_right = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instruments", x => x.id);
                    table.ForeignKey(
                        name: "fk_instruments_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_bars",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    close = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    volume_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_bars", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_bars_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_constituents",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    benchmark_id = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    weight = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_benchmark_constituents", x => x.id);
                    table.ForeignKey(
                        name: "fk_benchmark_constituents_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_benchmark_constituents_benchmarks_benchmark_id",
                        column: x => x.benchmark_id,
                        principalTable: "benchmarks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "funds",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    base_currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_demo = table.Column<bool>(type: "boolean", nullable: false),
                    benchmark_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funds", x => x.id);
                    table.ForeignKey(
                        name: "fk_funds_benchmarks_benchmark_id",
                        column: x => x.benchmark_id,
                        principalTable: "benchmarks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "sleeves",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fund_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mandate_type = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    benchmark_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sleeves", x => x.id);
                    table.ForeignKey(
                        name: "fk_sleeves_benchmarks_benchmark_id",
                        column: x => x.benchmark_id,
                        principalTable: "benchmarks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_sleeves_funds_fund_id",
                        column: x => x.fund_id,
                        principalTable: "funds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manager_accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sleeve_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    inception_date = table.Column<DateOnly>(type: "date", nullable: false),
                    allocated_capital_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    data_source = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manager_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_manager_accounts_sleeves_sleeve_id",
                        column: x => x.sleeve_id,
                        principalTable: "sleeves",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mandate_limits",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sleeve_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mandate_limits", x => x.id);
                    table.ForeignKey(
                        name: "fk_mandate_limits_sleeves_sleeve_id",
                        column: x => x.sleeve_id,
                        principalTable: "sleeves",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cash_flows",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manager_account_id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    amount_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_flows", x => x.id);
                    table.ForeignKey(
                        name: "fk_cash_flows_manager_accounts_manager_account_id",
                        column: x => x.manager_account_id,
                        principalTable: "manager_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nav_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manager_account_id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    nav_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nav_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_nav_snapshots_manager_accounts_manager_account_id",
                        column: x => x.manager_account_id,
                        principalTable: "manager_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "position_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manager_account_id = table.Column<int>(type: "integer", nullable: false),
                    instrument_id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    mark_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    underlying_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    delta = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    market_value_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    exposure_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    unrealized_pnl_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_position_snapshots_instruments_instrument_id",
                        column: x => x.instrument_id,
                        principalTable: "instruments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_position_snapshots_manager_accounts_manager_account_id",
                        column: x => x.manager_account_id,
                        principalTable: "manager_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_symbol",
                table: "assets",
                column: "symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_constituents_asset_id",
                table: "benchmark_constituents",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_constituents_benchmark_id_asset_id",
                table: "benchmark_constituents",
                columns: new[] { "benchmark_id", "asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmarks_name",
                table: "benchmarks",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cash_flows_manager_account_id_date",
                table: "cash_flows",
                columns: new[] { "manager_account_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_funds_benchmark_id",
                table: "funds",
                column: "benchmark_id");

            migrationBuilder.CreateIndex(
                name: "ix_funds_name",
                table: "funds",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instruments_asset_id",
                table: "instruments",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_instruments_symbol",
                table: "instruments",
                column: "symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_manager_accounts_sleeve_id_name",
                table: "manager_accounts",
                columns: new[] { "sleeve_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mandate_limits_sleeve_id_kind",
                table: "mandate_limits",
                columns: new[] { "sleeve_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nav_snapshots_manager_account_id_date",
                table: "nav_snapshots",
                columns: new[] { "manager_account_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_position_snapshots_date_instrument_id",
                table: "position_snapshots",
                columns: new[] { "date", "instrument_id" });

            migrationBuilder.CreateIndex(
                name: "ix_position_snapshots_instrument_id",
                table: "position_snapshots",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_position_snapshots_manager_account_id_date_instrument_id",
                table: "position_snapshots",
                columns: new[] { "manager_account_id", "date", "instrument_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_bars_asset_id_date",
                table: "price_bars",
                columns: new[] { "asset_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sleeves_benchmark_id",
                table: "sleeves",
                column: "benchmark_id");

            migrationBuilder.CreateIndex(
                name: "ix_sleeves_fund_id_name",
                table: "sleeves",
                columns: new[] { "fund_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_constituents");

            migrationBuilder.DropTable(
                name: "cash_flows");

            migrationBuilder.DropTable(
                name: "mandate_limits");

            migrationBuilder.DropTable(
                name: "nav_snapshots");

            migrationBuilder.DropTable(
                name: "position_snapshots");

            migrationBuilder.DropTable(
                name: "price_bars");

            migrationBuilder.DropTable(
                name: "instruments");

            migrationBuilder.DropTable(
                name: "manager_accounts");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "sleeves");

            migrationBuilder.DropTable(
                name: "funds");

            migrationBuilder.DropTable(
                name: "benchmarks");
        }
    }
}
