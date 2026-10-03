using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectStudio.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class C4_SitesAndLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    name_norm = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    company_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    overture_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    city = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    zip = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    county_fips = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    lat = table.Column<double>(type: "REAL", nullable: false),
                    lon = table.Column<double>(type: "REAL", nullable: false),
                    phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    website = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    taxonomy_primary = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    taxonomy_path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    basic_category = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    confidence = table.Column<double>(type: "REAL", nullable: false),
                    release = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.id);
                    table.ForeignKey(
                        name: "FK_sites_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                columns: table => new
                {
                    campaign_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    site_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    suppression_reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    dealer_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    branch_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    assignment = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leads", x => new { x.campaign_id, x.id });
                    table.ForeignKey(
                        name: "FK_leads_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_leads_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_records",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    site_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    source_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    retrieved_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    license = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    payload_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_records_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_companies_domain",
                table: "companies",
                column: "domain");

            migrationBuilder.CreateIndex(
                name: "ix_companies_name_norm",
                table: "companies",
                column: "name_norm");

            migrationBuilder.CreateIndex(
                name: "ix_leads_campaign_site",
                table: "leads",
                columns: new[] { "campaign_id", "site_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_campaign_status",
                table: "leads",
                columns: new[] { "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leads_site_id",
                table: "leads",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "IX_sites_company_id",
                table: "sites",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_sites_county_fips",
                table: "sites",
                column: "county_fips");

            migrationBuilder.CreateIndex(
                name: "ix_sites_overture_id",
                table: "sites",
                column: "overture_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_records_site",
                table: "source_records",
                columns: new[] { "site_id", "source" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leads");

            migrationBuilder.DropTable(
                name: "source_records");

            migrationBuilder.DropTable(
                name: "sites");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
