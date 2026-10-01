using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SourceSharp.Host.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    steam_id = table.Column<string>(type: "TEXT", nullable: false),
                    created = table.Column<long>(type: "INTEGER", nullable: false),
                    banned = table.Column<bool>(type: "INTEGER", nullable: false),
                    note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.steam_id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    actor = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    target = table.Column<string>(type: "TEXT", nullable: false),
                    before_json = table.Column<string>(type: "TEXT", nullable: true),
                    after_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "australium_ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    character_id = table.Column<string>(type: "TEXT", nullable: false),
                    delta = table.Column<long>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "TEXT", nullable: true),
                    at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_australium_ledger", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "character_levels",
                columns: table => new
                {
                    character_id = table.Column<string>(type: "TEXT", nullable: false),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    level_hash = table.Column<string>(type: "TEXT", nullable: false),
                    since = table.Column<long>(type: "INTEGER", nullable: false),
                    last_used = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_levels", x => new { x.character_id, x.depth });
                });

            migrationBuilder.CreateTable(
                name: "characters",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    account = table.Column<string>(type: "TEXT", nullable: false),
                    class_name = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    hardcore = table.Column<bool>(type: "INTEGER", nullable: false),
                    fallen = table.Column<bool>(type: "INTEGER", nullable: false),
                    level = table.Column<int>(type: "INTEGER", nullable: false),
                    xp = table.Column<long>(type: "INTEGER", nullable: false),
                    reached_depth = table.Column<int>(type: "INTEGER", nullable: false),
                    difficulty = table.Column<int>(type: "INTEGER", nullable: false),
                    sheet = table.Column<byte[]>(type: "BLOB", nullable: false),
                    sheet_version = table.Column<int>(type: "INTEGER", nullable: false),
                    australium = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_characters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency",
                columns: table => new
                {
                    request_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: true),
                    response = table.Column<byte[]>(type: "BLOB", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "instances",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    party_id = table.Column<string>(type: "TEXT", nullable: true),
                    level_hash = table.Column<string>(type: "TEXT", nullable: true),
                    pod_name = table.Column<string>(type: "TEXT", nullable: true),
                    pod_uid = table.Column<string>(type: "TEXT", nullable: true),
                    pod_ip = table.Column<string>(type: "TEXT", nullable: true),
                    port = table.Column<int>(type: "INTEGER", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    mod_image = table.Column<string>(type: "TEXT", nullable: true),
                    mod_image_digest = table.Column<string>(type: "TEXT", nullable: true),
                    rules_sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    seed = table.Column<long>(type: "INTEGER", nullable: false),
                    created = table.Column<long>(type: "INTEGER", nullable: false),
                    booted = table.Column<long>(type: "INTEGER", nullable: true),
                    live_at = table.Column<long>(type: "INTEGER", nullable: true),
                    drain_at = table.Column<long>(type: "INTEGER", nullable: true),
                    reaped_at = table.Column<long>(type: "INTEGER", nullable: true),
                    exit_code = table.Column<int>(type: "INTEGER", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    last_heartbeat = table.Column<long>(type: "INTEGER", nullable: true),
                    players = table.Column<int>(type: "INTEGER", nullable: false),
                    expected_port = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "item_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    item_id = table.Column<string>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    from_owner = table.Column<string>(type: "TEXT", nullable: true),
                    to_owner = table.Column<string>(type: "TEXT", nullable: true),
                    instance_id = table.Column<string>(type: "TEXT", nullable: true),
                    request_id = table.Column<string>(type: "TEXT", nullable: true),
                    actor = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    seed = table.Column<long>(type: "INTEGER", nullable: false),
                    base_type = table.Column<string>(type: "TEXT", nullable: false),
                    rarity = table.Column<int>(type: "INTEGER", nullable: false),
                    item_level = table.Column<int>(type: "INTEGER", nullable: false),
                    count = table.Column<int>(type: "INTEGER", nullable: false),
                    identified = table.Column<bool>(type: "INTEGER", nullable: false),
                    instance = table.Column<byte[]>(type: "BLOB", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    for_character = table.Column<string>(type: "TEXT", nullable: true),
                    owner_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", nullable: false),
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: true),
                    tier = table.Column<int>(type: "INTEGER", nullable: false),
                    rolled_for_level = table.Column<int>(type: "INTEGER", nullable: false),
                    minted_by = table.Column<string>(type: "TEXT", nullable: false),
                    minted_at = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    terminal_reason = table.Column<string>(type: "TEXT", nullable: true),
                    terminal_at = table.Column<long>(type: "INTEGER", nullable: true),
                    admin_edited = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leases",
                columns: table => new
                {
                    character_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    token = table.Column<byte[]>(type: "BLOB", nullable: false),
                    issued = table.Column<long>(type: "INTEGER", nullable: false),
                    expires = table.Column<long>(type: "INTEGER", nullable: false),
                    heartbeat = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leases", x => x.character_id);
                });

            migrationBuilder.CreateTable(
                name: "levels",
                columns: table => new
                {
                    hash = table.Column<string>(type: "TEXT", nullable: false),
                    pack_id = table.Column<long>(type: "INTEGER", nullable: false),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    tileset = table.Column<string>(type: "TEXT", nullable: false),
                    seed = table.Column<long>(type: "INTEGER", nullable: false),
                    difficulty = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    created = table.Column<long>(type: "INTEGER", nullable: false),
                    handed_to = table.Column<string>(type: "TEXT", nullable: true),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    rules_sha256 = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_levels", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "lost_and_found",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    character_id = table.Column<string>(type: "TEXT", nullable: false),
                    item_id = table.Column<string>(type: "TEXT", nullable: false),
                    fee = table.Column<long>(type: "INTEGER", nullable: false),
                    since = table.Column<long>(type: "INTEGER", nullable: false),
                    origin_instance = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lost_and_found", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "map_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    created = table.Column<long>(type: "INTEGER", nullable: false),
                    started = table.Column<long>(type: "INTEGER", nullable: true),
                    finished = table.Column<long>(type: "INTEGER", nullable: true),
                    log = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_map_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pack_assignments",
                columns: table => new
                {
                    mod = table.Column<string>(type: "TEXT", nullable: false),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    pack_id = table.Column<long>(type: "INTEGER", nullable: false),
                    pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    assigned_by = table.Column<string>(type: "TEXT", nullable: false),
                    assigned_at = table.Column<long>(type: "INTEGER", nullable: false),
                    old_levels = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pack_assignments", x => new { x.mod, x.depth });
                });

            migrationBuilder.CreateTable(
                name: "packs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    mod = table.Column<string>(type: "TEXT", nullable: false),
                    library = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    pack_id = table.Column<string>(type: "TEXT", nullable: false),
                    library_version = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    uploaded_by = table.Column<string>(type: "TEXT", nullable: false),
                    uploaded_at = table.Column<long>(type: "INTEGER", nullable: false),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    lint_json = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_packs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "parties",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    leader = table.Column<string>(type: "TEXT", nullable: false),
                    created = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parties", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "party_members",
                columns: table => new
                {
                    party_id = table.Column<string>(type: "TEXT", nullable: false),
                    character_id = table.Column<string>(type: "TEXT", nullable: false),
                    invited = table.Column<bool>(type: "INTEGER", nullable: false),
                    since = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_members", x => new { x.party_id, x.character_id });
                });

            migrationBuilder.CreateTable(
                name: "players",
                columns: table => new
                {
                    steam_id = table.Column<string>(type: "TEXT", nullable: false),
                    pinned_peer = table.Column<string>(type: "TEXT", nullable: false),
                    allocator = table.Column<string>(type: "TEXT", nullable: false),
                    since = table.Column<long>(type: "INTEGER", nullable: false),
                    last_seen = table.Column<long>(type: "INTEGER", nullable: false),
                    previous_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_players", x => x.steam_id);
                });

            migrationBuilder.CreateTable(
                name: "rules_modules",
                columns: table => new
                {
                    sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    assembly = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    contract_version = table.Column<string>(type: "TEXT", nullable: false),
                    deps_json = table.Column<string>(type: "TEXT", nullable: false),
                    first_seen = table.Column<long>(type: "INTEGER", nullable: false),
                    first_instance = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rules_modules", x => x.sha256);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    client_addr = table.Column<string>(type: "TEXT", nullable: false),
                    steam_id = table.Column<string>(type: "TEXT", nullable: true),
                    instance_id = table.Column<string>(type: "TEXT", nullable: true),
                    backend = table.Column<string>(type: "TEXT", nullable: false),
                    peer = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    opened = table.Column<long>(type: "INTEGER", nullable: false),
                    last_seen = table.Column<long>(type: "INTEGER", nullable: false),
                    identified_at = table.Column<long>(type: "INTEGER", nullable: true),
                    close_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value_json = table.Column<string>(type: "TEXT", nullable: false),
                    updated_by = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    hub_instance = table.Column<string>(type: "TEXT", nullable: false),
                    a = table.Column<string>(type: "TEXT", nullable: false),
                    b = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    offer_json = table.Column<string>(type: "TEXT", nullable: false),
                    locked_a = table.Column<bool>(type: "INTEGER", nullable: false),
                    locked_b = table.Column<bool>(type: "INTEGER", nullable: false),
                    confirmed_a = table.Column<bool>(type: "INTEGER", nullable: false),
                    confirmed_b = table.Column<bool>(type: "INTEGER", nullable: false),
                    committed = table.Column<long>(type: "INTEGER", nullable: true),
                    created = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trades", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vendor_stock",
                columns: table => new
                {
                    hub_instance = table.Column<string>(type: "TEXT", nullable: false),
                    vendor = table.Column<string>(type: "TEXT", nullable: false),
                    item_id = table.Column<string>(type: "TEXT", nullable: false),
                    rolled_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_stock", x => new { x.hub_instance, x.vendor, x.item_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_action",
                table: "audit_log",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_australium_ledger_character_id",
                table: "australium_ledger",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "IX_character_levels_level_hash",
                table: "character_levels",
                column: "level_hash");

            migrationBuilder.CreateIndex(
                name: "IX_characters_account",
                table: "characters",
                column: "account");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_at",
                table: "idempotency",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_instances_pod_name",
                table: "instances",
                column: "pod_name");

            migrationBuilder.CreateIndex(
                name: "IX_instances_state",
                table: "instances",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_item_events_item_id",
                table: "item_events",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "IX_items_base_type",
                table: "items",
                column: "base_type");

            migrationBuilder.CreateIndex(
                name: "IX_items_instance_id_state",
                table: "items",
                columns: new[] { "instance_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_items_owner_kind_owner_id",
                table: "items",
                columns: new[] { "owner_kind", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_items_rarity",
                table: "items",
                column: "rarity");

            migrationBuilder.CreateIndex(
                name: "IX_leases_instance_id",
                table: "leases",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "IX_levels_depth_pack_id_state",
                table: "levels",
                columns: new[] { "depth", "pack_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_lost_and_found_character_id",
                table: "lost_and_found",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "IX_lost_and_found_item_id",
                table: "lost_and_found",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_map_jobs_key",
                table: "map_jobs",
                column: "key");

            migrationBuilder.CreateIndex(
                name: "IX_map_jobs_state_priority",
                table: "map_jobs",
                columns: new[] { "state", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_packs_mod_library_version",
                table: "packs",
                columns: new[] { "mod", "library", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_party_members_character_id",
                table: "party_members",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "IX_players_pinned_peer",
                table: "players",
                column: "pinned_peer");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_client_addr",
                table: "sessions",
                column: "client_addr");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_peer",
                table: "sessions",
                column: "peer");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_steam_id",
                table: "sessions",
                column: "steam_id");

            migrationBuilder.CreateIndex(
                name: "IX_trades_hub_instance_state",
                table: "trades",
                columns: new[] { "hub_instance", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "australium_ledger");

            migrationBuilder.DropTable(
                name: "character_levels");

            migrationBuilder.DropTable(
                name: "characters");

            migrationBuilder.DropTable(
                name: "idempotency");

            migrationBuilder.DropTable(
                name: "instances");

            migrationBuilder.DropTable(
                name: "item_events");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "leases");

            migrationBuilder.DropTable(
                name: "levels");

            migrationBuilder.DropTable(
                name: "lost_and_found");

            migrationBuilder.DropTable(
                name: "map_jobs");

            migrationBuilder.DropTable(
                name: "pack_assignments");

            migrationBuilder.DropTable(
                name: "packs");

            migrationBuilder.DropTable(
                name: "parties");

            migrationBuilder.DropTable(
                name: "party_members");

            migrationBuilder.DropTable(
                name: "players");

            migrationBuilder.DropTable(
                name: "rules_modules");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "trades");

            migrationBuilder.DropTable(
                name: "vendor_stock");
        }
    }
}
