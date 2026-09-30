using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectStudio.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class C1_Campaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    product = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    folder_path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    profile_json = table.Column<string>(type: "TEXT", nullable: true),
                    profile_saved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    geo_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_slug",
                table: "campaigns",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaigns");
        }
    }
}
