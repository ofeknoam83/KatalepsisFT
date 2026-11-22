using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KatalepsisFT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Alerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fund_id = table.Column<int>(type: "integer", nullable: false),
                    scope_kind = table.Column<int>(type: "integer", nullable: false),
                    scope_id = table.Column<int>(type: "integer", nullable: false),
                    metric = table.Column<int>(type: "integer", nullable: false),
                    @operator = table.Column<int>(name: "operator", type: "integer", nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mandate_limit_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_rules_funds_fund_id",
                        column: x => x.fund_id,
                        principalTable: "funds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_rules_mandate_limits_mandate_limit_id",
                        column: x => x.mandate_limit_id,
                        principalTable: "mandate_limits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rule_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    value = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    data_date = table.Column<DateOnly>(type: "date", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_while_breaching = table.Column<bool>(type: "boolean", nullable: false),
                    assignee_user_id = table.Column<int>(type: "integer", nullable: true),
                    assignee_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    snoozed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_alert_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_actions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    alert_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_actions", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_actions_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_actions_alert_id",
                table: "alert_actions",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_fund_id",
                table: "alert_rules",
                column: "fund_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_mandate_limit_id",
                table: "alert_rules",
                column: "mandate_limit_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alerts_rule_id_status",
                table: "alerts",
                columns: new[] { "rule_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_actions");

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "alert_rules");
        }
    }
}
