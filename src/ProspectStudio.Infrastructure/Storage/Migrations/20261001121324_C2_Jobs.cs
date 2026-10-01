using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectStudio.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class C2_Jobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    campaign_id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    progress = table.Column<double>(type: "REAL", nullable: false),
                    message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    params_json = table.Column<string>(type: "TEXT", nullable: true),
                    result_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_campaign_status",
                table: "jobs",
                columns: new[] { "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_created_at",
                table: "jobs",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
