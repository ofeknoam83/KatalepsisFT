using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_positions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manager_account_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    entry_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    strike = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    option_right = table.Column<int>(type: "integer", nullable: true),
                    delta = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    underlying_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_positions", x => x.id);
                    table.ForeignKey(
                        name: "fk_manual_positions_manager_accounts_manager_account_id",
                        column: x => x.manager_account_id,
                        principalTable: "manager_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_manual_positions_manager_account_id",
                table: "manual_positions",
                column: "manager_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_positions");
        }
    }
}
