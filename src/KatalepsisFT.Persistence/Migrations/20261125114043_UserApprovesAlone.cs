using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserApprovesAlone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "approves_alone",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approves_alone",
                table: "users");
        }
    }
}
