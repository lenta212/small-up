using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class LuaMDeepCryoPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "lifecycle_revision",
                table: "profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "luam_deep_cryo_snapshot",
                columns: table => new
                {
                    lua_m_deep_cryo_snapshots_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    player_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    preference_id = table.Column<int>(type: "INTEGER", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    source_round_id = table.Column<int>(type: "INTEGER", nullable: false),
                    last_restore_round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    format_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    payload_size_bytes = table.Column<int>(type: "INTEGER", nullable: false),
                    entity_count = table.Column<int>(type: "INTEGER", nullable: false),
                    source_build_version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    prototype_manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    stored_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    consumed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    quarantined_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    quarantine_reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_deep_cryo_snapshot", x => x.lua_m_deep_cryo_snapshots_id);
                    table.UniqueConstraint("ak_luam_deep_cryo_snapshot_lua_m_deep_cryo_snapshots_id_profile_id", x => new { x.lua_m_deep_cryo_snapshots_id, x.profile_id });
                    table.CheckConstraint("CK_luam_cryo_snapshot_dates", "stored_at_utc <= updated_at_utc");
                    table.CheckConstraint("CK_luam_cryo_snapshot_format", "format_version > 0");
                    table.CheckConstraint("CK_luam_cryo_snapshot_hashes", "length(payload_hash) = 64 AND length(prototype_manifest_hash) = 64");
                    table.CheckConstraint("CK_luam_cryo_snapshot_identity", "slot >= 0 AND source_round_id >= 0");
                    table.CheckConstraint("CK_luam_cryo_snapshot_payload", "payload_size_bytes > 0 AND entity_count > 0 AND length(payload) = payload_size_bytes");
                    table.CheckConstraint("CK_luam_cryo_snapshot_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_cryo_snapshot_status", "status >= 0 AND status <= 3");
                    table.CheckConstraint("CK_luam_cryo_snapshot_terminal", "(status = 2 AND consumed_at_utc IS NOT NULL AND quarantined_at_utc IS NULL AND quarantine_reason IS NULL) OR (status = 3 AND consumed_at_utc IS NULL AND quarantined_at_utc IS NOT NULL AND quarantine_reason IS NOT NULL) OR (status IN (0, 1) AND consumed_at_utc IS NULL AND quarantined_at_utc IS NULL AND quarantine_reason IS NULL)");
                    table.ForeignKey(
                        name: "FK_luam_deep_cryo_snapshot_preference_preference_id",
                        column: x => x.preference_id,
                        principalTable: "preference",
                        principalColumn: "preference_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_deep_cryo_snapshot_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_character_presence_lease",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    server_instance_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    restore_round_id = table.Column<int>(type: "INTEGER", nullable: false),
                    acquired_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    renewed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_character_presence_lease", x => x.profile_id);
                    table.CheckConstraint("CK_luam_cryo_lease_dates", "acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc");
                    table.CheckConstraint("CK_luam_cryo_lease_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_cryo_lease_round", "restore_round_id >= 0");
                    table.ForeignKey(
                        name: "FK_luam_character_presence_lease_luam_deep_cryo_snapshot_lua_m_deep_cryo_snapshot_id",
                        columns: x => new { x.snapshot_id, x.profile_id },
                        principalTable: "luam_deep_cryo_snapshot",
                        principalColumns: new[] { "lua_m_deep_cryo_snapshots_id", "profile_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_character_presence_lease_profile_profile_id1",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_deep_cryo_operation",
                columns: table => new
                {
                    lua_m_deep_cryo_operations_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    operation_identity_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    snapshot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    result_status = table.Column<int>(type: "INTEGER", nullable: false),
                    result_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_deep_cryo_operation", x => x.lua_m_deep_cryo_operations_id);
                    table.CheckConstraint("CK_luam_cryo_operation_identity", "length(operation_identity_key) = 64");
                    table.CheckConstraint("CK_luam_cryo_operation_kind", "kind >= 0 AND kind <= 6");
                    table.CheckConstraint("CK_luam_cryo_operation_revision", "result_revision >= 0");
                    table.CheckConstraint("CK_luam_cryo_operation_round", "round_id IS NULL OR round_id >= 0");
                    table.CheckConstraint("CK_luam_cryo_operation_status", "result_status >= 0 AND result_status <= 3");
                    table.ForeignKey(
                        name: "FK_luam_deep_cryo_operation_luam_deep_cryo_snapshot_lua_m_deep_cryo_snapshot_id",
                        columns: x => new { x.snapshot_id, x.profile_id },
                        principalTable: "luam_deep_cryo_snapshot",
                        principalColumns: new[] { "lua_m_deep_cryo_snapshots_id", "profile_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_deep_cryo_operation_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_luam_character_presence_lease_snapshot_id_profile_id",
                table: "luam_character_presence_lease",
                columns: new[] { "snapshot_id", "profile_id" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_cryo_lease_expires",
                table: "luam_character_presence_lease",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "UX_luam_cryo_lease_snapshot",
                table: "luam_character_presence_lease",
                column: "snapshot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_luam_cryo_lease_token",
                table: "luam_character_presence_lease",
                column: "lease_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_cryo_operation_profile_created",
                table: "luam_deep_cryo_operation",
                columns: new[] { "profile_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_cryo_operation_snapshot_created",
                table: "luam_deep_cryo_operation",
                columns: new[] { "snapshot_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_deep_cryo_operation_snapshot_id_profile_id",
                table: "luam_deep_cryo_operation",
                columns: new[] { "snapshot_id", "profile_id" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_cryo_operation_claim_lease",
                table: "luam_deep_cryo_operation",
                column: "lease_id",
                unique: true,
                filter: "kind = 1");

            migrationBuilder.CreateIndex(
                name: "UX_luam_cryo_operation_id",
                table: "luam_deep_cryo_operation",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_cryo_snapshot_player_slot_status",
                table: "luam_deep_cryo_snapshot",
                columns: new[] { "player_user_id", "slot", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_cryo_snapshot_profile_stored",
                table: "luam_deep_cryo_snapshot",
                columns: new[] { "profile_id", "stored_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_deep_cryo_snapshot_preference_id",
                table: "luam_deep_cryo_snapshot",
                column: "preference_id");

            migrationBuilder.CreateIndex(
                name: "UX_luam_cryo_snapshot_active_profile",
                table: "luam_deep_cryo_snapshot",
                column: "profile_id",
                unique: true,
                filter: "status <> 2");

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_profile_lifecycle_revision_nonnegative_insert
                BEFORE INSERT ON profile
                WHEN NEW.lifecycle_revision < 0
                BEGIN
                    SELECT RAISE(ABORT, 'Profile lifecycle revision cannot be negative');
                END;

                CREATE TRIGGER TR_profile_lifecycle_revision_nonnegative_update
                BEFORE UPDATE OF lifecycle_revision ON profile
                WHEN NEW.lifecycle_revision < 0
                BEGIN
                    SELECT RAISE(ABORT, 'Profile lifecycle revision cannot be negative');
                END;

                CREATE TRIGGER TR_luam_cryo_operation_no_update
                BEFORE UPDATE ON luam_deep_cryo_operation
                BEGIN
                    SELECT RAISE(ABORT, 'LuaM deep-cryo operation journal is append-only');
                END;

                CREATE TRIGGER TR_luam_cryo_operation_no_delete
                BEFORE DELETE ON luam_deep_cryo_operation
                BEGIN
                    SELECT RAISE(ABORT, 'LuaM deep-cryo operation journal is append-only');
                END;

                CREATE TRIGGER TR_luam_cryo_snapshot_immutable_payload
                BEFORE UPDATE ON luam_deep_cryo_snapshot
                WHEN OLD.player_user_id IS NOT NEW.player_user_id
                  OR OLD.preference_id IS NOT NEW.preference_id
                  OR OLD.profile_id IS NOT NEW.profile_id
                  OR OLD.slot IS NOT NEW.slot
                  OR OLD.source_round_id IS NOT NEW.source_round_id
                  OR OLD.format_version IS NOT NEW.format_version
                  OR OLD.payload IS NOT NEW.payload
                  OR OLD.payload_hash IS NOT NEW.payload_hash
                  OR OLD.payload_size_bytes IS NOT NEW.payload_size_bytes
                  OR OLD.entity_count IS NOT NEW.entity_count
                  OR OLD.source_build_version IS NOT NEW.source_build_version
                  OR OLD.prototype_manifest_hash IS NOT NEW.prototype_manifest_hash
                  OR OLD.stored_at_utc IS NOT NEW.stored_at_utc
                BEGIN
                    SELECT RAISE(ABORT, 'LuaM deep-cryo snapshot identity and payload are immutable');
                END;

                CREATE TRIGGER TR_luam_cryo_snapshot_no_delete
                BEFORE DELETE ON luam_deep_cryo_snapshot
                BEGIN
                    SELECT RAISE(ABORT, 'LuaM deep-cryo snapshots cannot be deleted');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __luam_deep_cryo_downgrade_guard (
                    value INTEGER NOT NULL CHECK (value = 0)
                );
                INSERT INTO __luam_deep_cryo_downgrade_guard (value)
                SELECT 1
                WHERE EXISTS (SELECT 1 FROM luam_deep_cryo_snapshot)
                   OR EXISTS (SELECT 1 FROM luam_character_presence_lease)
                   OR EXISTS (SELECT 1 FROM luam_deep_cryo_operation);
                DROP TABLE __luam_deep_cryo_downgrade_guard;
                """);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_operation_no_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_operation_no_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_snapshot_immutable_payload;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_snapshot_no_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_profile_lifecycle_revision_nonnegative_insert;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_profile_lifecycle_revision_nonnegative_update;");

            migrationBuilder.DropTable(
                name: "luam_character_presence_lease");

            migrationBuilder.DropTable(
                name: "luam_deep_cryo_operation");

            migrationBuilder.DropTable(
                name: "luam_deep_cryo_snapshot");

            // EF's SQLite DropColumn path rebuilds profile and splits the migration
            // around raw trigger SQL. Bundled SQLite supports native DROP COLUMN,
            // which keeps this downgrade transactional and rerunnable.
            migrationBuilder.Sql("ALTER TABLE profile DROP COLUMN lifecycle_revision;");
        }
    }
}
