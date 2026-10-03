using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SourceSharp.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class Anchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "anchors",
                columns: table => new
                {
                    character_id = table.Column<string>(type: "TEXT", nullable: false),
                    level_id = table.Column<string>(type: "TEXT", nullable: false),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    x = table.Column<float>(type: "REAL", nullable: false),
                    y = table.Column<float>(type: "REAL", nullable: false),
                    z = table.Column<float>(type: "REAL", nullable: false),
                    yaw = table.Column<float>(type: "REAL", nullable: false),
                    placed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_anchors", x => x.character_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_anchors_level_id",
                table: "anchors",
                column: "level_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anchors");
        }
    }
}
