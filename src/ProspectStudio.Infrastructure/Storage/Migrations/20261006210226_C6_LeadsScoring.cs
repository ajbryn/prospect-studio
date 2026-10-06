using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectStudio.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class C6_LeadsScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cohort",
                table: "leads",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                table: "leads",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_title",
                table: "leads",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "features_json",
                table: "leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "leads",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "research_status",
                table: "leads",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<int>(
                name: "score",
                table: "leads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "score_breakdown_json",
                table: "leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tier",
                table: "leads",
                type: "TEXT",
                maxLength: 1,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scoring_weights_json",
                table: "campaigns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "research",
                columns: table => new
                {
                    campaign_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    lead_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    research_json = table.Column<string>(type: "TEXT", nullable: false),
                    llm_adjustment = table.Column<int>(type: "INTEGER", nullable: false),
                    saved_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_research", x => new { x.campaign_id, x.lead_id });
                    table.ForeignKey(
                        name: "FK_research_leads_campaign_id_lead_id",
                        columns: x => new { x.campaign_id, x.lead_id },
                        principalTable: "leads",
                        principalColumns: new[] { "campaign_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signals",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    campaign_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    lead_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signals", x => x.id);
                    table.ForeignKey(
                        name: "FK_signals_leads_campaign_id_lead_id",
                        columns: x => new { x.campaign_id, x.lead_id },
                        principalTable: "leads",
                        principalColumns: new[] { "campaign_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_campaign_score",
                table: "leads",
                columns: new[] { "campaign_id", "score" });

            migrationBuilder.CreateIndex(
                name: "ix_signals_campaign_lead",
                table: "signals",
                columns: new[] { "campaign_id", "lead_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "research");

            migrationBuilder.DropTable(
                name: "signals");

            migrationBuilder.DropIndex(
                name: "ix_leads_campaign_score",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "cohort",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "contact_name",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "contact_title",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "features_json",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "notes",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "research_status",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "score",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "score_breakdown_json",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "tier",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "scoring_weights_json",
                table: "campaigns");
        }
    }
}
