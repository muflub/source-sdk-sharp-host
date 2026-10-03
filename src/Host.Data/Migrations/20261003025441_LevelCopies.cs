using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SourceSharp.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class LevelCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "level_hash",
                table: "character_levels",
                newName: "level_id");

            migrationBuilder.RenameIndex(
                name: "IX_character_levels_level_hash",
                table: "character_levels",
                newName: "IX_character_levels_level_id");

            migrationBuilder.AddColumn<string>(
                name: "level_id",
                table: "instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "level_id",
                table: "instances");

            migrationBuilder.RenameColumn(
                name: "level_id",
                table: "character_levels",
                newName: "level_hash");

            migrationBuilder.RenameIndex(
                name: "IX_character_levels_level_id",
                table: "character_levels",
                newName: "IX_character_levels_level_hash");
        }
    }
}
