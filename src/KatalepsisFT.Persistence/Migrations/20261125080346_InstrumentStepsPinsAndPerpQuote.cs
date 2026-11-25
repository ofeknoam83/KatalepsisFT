using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstrumentStepsPinsAndPerpQuote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_trading_accounts_manager_account_id_venue",
                table: "trading_accounts");

            migrationBuilder.DropIndex(
                name: "ix_listed_instruments_venue_type_asset",
                table: "listed_instruments");

            migrationBuilder.AddColumn<string>(
                name: "perp_quote",
                table: "trading_accounts",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "USDT");

            migrationBuilder.AddColumn<decimal>(
                name: "min_quantity",
                table: "listed_instruments",
                type: "numeric(28,14)",
                precision: 28,
                scale: 14,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_step",
                table: "listed_instruments",
                type: "numeric(28,14)",
                precision: 28,
                scale: 14,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "quantity_step",
                table: "listed_instruments",
                type: "numeric(28,14)",
                precision: 28,
                scale: 14,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pinned_instruments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fund_id = table.Column<int>(type: "integer", nullable: false),
                    asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pinned_instruments", x => x.id);
                    table.ForeignKey(
                        name: "fk_pinned_instruments_funds_fund_id",
                        column: x => x.fund_id,
                        principalTable: "funds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_trading_accounts_manager_account_id_venue_perp_quote",
                table: "trading_accounts",
                columns: new[] { "manager_account_id", "venue", "perp_quote" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listed_instruments_venue_type_asset_quote",
                table: "listed_instruments",
                columns: new[] { "venue", "type", "asset", "quote" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pinned_instruments_fund_id_asset_type",
                table: "pinned_instruments",
                columns: new[] { "fund_id", "asset", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pinned_instruments");

            migrationBuilder.DropIndex(
                name: "ix_trading_accounts_manager_account_id_venue_perp_quote",
                table: "trading_accounts");

            migrationBuilder.DropIndex(
                name: "ix_listed_instruments_venue_type_asset_quote",
                table: "listed_instruments");

            migrationBuilder.DropColumn(
                name: "perp_quote",
                table: "trading_accounts");

            migrationBuilder.DropColumn(
                name: "min_quantity",
                table: "listed_instruments");

            migrationBuilder.DropColumn(
                name: "price_step",
                table: "listed_instruments");

            migrationBuilder.DropColumn(
                name: "quantity_step",
                table: "listed_instruments");

            migrationBuilder.CreateIndex(
                name: "ix_trading_accounts_manager_account_id_venue",
                table: "trading_accounts",
                columns: new[] { "manager_account_id", "venue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listed_instruments_venue_type_asset",
                table: "listed_instruments",
                columns: new[] { "venue", "type", "asset" },
                unique: true);
        }
    }
}
