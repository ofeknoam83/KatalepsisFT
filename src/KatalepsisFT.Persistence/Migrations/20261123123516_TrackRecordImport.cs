using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackRecordImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_imported",
                table: "nav_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_book_change",
                table: "cash_flows",
                type: "boolean",
                nullable: false,
                defaultValue: false);
                    // Flows the system had already booked for accounts entering or leaving a book are told apart by the note they were given.
            migrationBuilder.Sql(
                "UPDATE cash_flows SET is_book_change = TRUE WHERE external_id IS NULL AND (note LIKE '%added to the book' OR note LIKE '%taken out of the book' OR note LIKE 'Manager retired:%' OR note LIKE 'Manager reinstated:%');"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_imported",
                table: "nav_snapshots");

            migrationBuilder.DropColumn(
                name: "is_book_change",
                table: "cash_flows");
        }
    }
}
