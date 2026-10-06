using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectStudio.Infrastructure.Storage.Migrations
{
    /// <inheritdoc />
    public partial class C5_DealersAndSuppression : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pre_suppression_status",
                table: "leads",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suppression_id",
                table: "leads",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dealers",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    website = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    alert_email = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    logo_file = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dealers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "suppression",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    company_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    name_norm = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    address_norm = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    zip = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    source_file = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppression", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dealer_branches",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    dealer_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    city = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    zip = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    lat = table.Column<double>(type: "REAL", nullable: false),
                    lon = table.Column<double>(type: "REAL", nullable: false),
                    phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    tracking_phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dealer_branches", x => x.id);
                    table.ForeignKey(
                        name: "FK_dealer_branches_dealers_dealer_id",
                        column: x => x.dealer_id,
                        principalTable: "dealers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "territories",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    dealer_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    branch_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    level = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_territories", x => x.id);
                    table.ForeignKey(
                        name: "FK_territories_dealers_dealer_id",
                        column: x => x.dealer_id,
                        principalTable: "dealers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dealer_branches_dealer_id",
                table: "dealer_branches",
                column: "dealer_id");

            migrationBuilder.CreateIndex(
                name: "ix_suppression_domain",
                table: "suppression",
                column: "domain");

            migrationBuilder.CreateIndex(
                name: "ix_suppression_name_norm",
                table: "suppression",
                column: "name_norm");

            migrationBuilder.CreateIndex(
                name: "IX_territories_dealer_id",
                table: "territories",
                column: "dealer_id");

            migrationBuilder.CreateIndex(
                name: "ix_territories_level_code",
                table: "territories",
                columns: new[] { "level", "code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dealer_branches");

            migrationBuilder.DropTable(
                name: "suppression");

            migrationBuilder.DropTable(
                name: "territories");

            migrationBuilder.DropTable(
                name: "dealers");

            migrationBuilder.DropColumn(
                name: "pre_suppression_status",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "suppression_id",
                table: "leads");
        }
    }
}
