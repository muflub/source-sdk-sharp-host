using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SourceSharp.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemContainers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "container",
                table: "items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "container",
                table: "items");
        }
    }
}
