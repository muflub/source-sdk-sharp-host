using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SourceSharp.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class SteamAuthAndBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bans",
                columns: table => new
                {
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false),
                    until = table.Column<long>(type: "INTEGER", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    created = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bans", x => new { x.kind, x.value });
                });

            migrationBuilder.CreateTable(
                name: "steam_auth",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", nullable: false),
                    steam_id = table.Column<string>(type: "TEXT", nullable: false),
                    ticket_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    gs_steam_id = table.Column<long>(type: "INTEGER", nullable: false),
                    epoch = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    validated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_auth", x => x.session_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bans_until",
                table: "bans",
                column: "until");

            migrationBuilder.CreateIndex(
                name: "IX_steam_auth_steam_id",
                table: "steam_auth",
                column: "steam_id");

            // plan_steam_auth S-14: the bans table becomes the one list. Carry the admin's address bans (the
            // "gateway.bans" setting, a JSON array of strings) and every banned account, then drop the setting.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO bans (kind, value, until, source, reason, created)
                SELECT 'address', j.value, NULL, 'admin', 'carried from the gateway.bans setting', s.updated_at
                FROM settings s, json_each(s.value_json) j
                WHERE s.key = 'gateway.bans' AND j.type = 'text';
                """);
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO bans (kind, value, until, source, reason, created)
                SELECT 'steamid', steam_id, NULL, 'admin', COALESCE(NULLIF(note, ''), 'carried from accounts.banned'), created
                FROM accounts WHERE banned = 1;
                """);
            migrationBuilder.Sql("DELETE FROM settings WHERE key = 'gateway.bans';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The address bans go back to the setting they came from; account bans never left accounts.banned.
            migrationBuilder.Sql("""
                INSERT OR REPLACE INTO settings (key, value_json, updated_by, updated_at)
                SELECT 'gateway.bans', json_group_array(value), 'migration', CAST(strftime('%s', 'now') AS INTEGER) * 1000
                FROM bans WHERE kind = 'address' AND until IS NULL
                HAVING COUNT(*) > 0;
                """);
            migrationBuilder.DropTable(
                name: "bans");

            migrationBuilder.DropTable(
                name: "steam_auth");
        }
    }
}
