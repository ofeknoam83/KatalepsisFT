using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ListedInstruments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "listed_instruments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    venue = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quote = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tradable = table.Column<bool>(type: "boolean", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_listed_instruments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_listed_instruments_asset",
                table: "listed_instruments",
                column: "asset");

            migrationBuilder.CreateIndex(
                name: "ix_listed_instruments_venue_type_asset",
                table: "listed_instruments",
                columns: new[] { "venue", "type", "asset" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listed_instruments");
        }
    }
}
