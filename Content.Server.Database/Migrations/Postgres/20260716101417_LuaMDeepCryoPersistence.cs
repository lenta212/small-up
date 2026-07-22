using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
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
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "luam_deep_cryo_snapshot",
                columns: table => new
                {
                    lua_m_deep_cryo_snapshots_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    player_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preference_id = table.Column<int>(type: "integer", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    source_round_id = table.Column<int>(type: "integer", nullable: false),
                    last_restore_round_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    entity_count = table.Column<int>(type: "integer", nullable: false),
                    source_build_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prototype_manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stored_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    quarantined_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_deep_cryo_snapshot", x => x.lua_m_deep_cryo_snapshots_id);
                    table.UniqueConstraint("ak_luam_deep_cryo_snapshot_lua_m_deep_cryo_snapshots_id_profil~", x => new { x.lua_m_deep_cryo_snapshots_id, x.profile_id });
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
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_instance_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    restore_round_id = table.Column<int>(type: "integer", nullable: false),
                    acquired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    renewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_character_presence_lease", x => x.profile_id);
                    table.CheckConstraint("CK_luam_cryo_lease_dates", "acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc");
                    table.CheckConstraint("CK_luam_cryo_lease_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_cryo_lease_round", "restore_round_id >= 0");
                    table.ForeignKey(
                        name: "FK_luam_character_presence_lease_luam_deep_cryo_snapshot_lua_m~",
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
                    lua_m_deep_cryo_operations_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_identity_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    result_status = table.Column<int>(type: "integer", nullable: false),
                    result_revision = table.Column<long>(type: "bigint", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                        name: "FK_luam_deep_cryo_operation_luam_deep_cryo_snapshot_lua_m_deep~",
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
                CREATE FUNCTION luam_reject_negative_profile_lifecycle_revision()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $luam$
                BEGIN
                    IF NEW.lifecycle_revision < 0 THEN
                        RAISE EXCEPTION 'Profile lifecycle revision cannot be negative';
                    END IF;
                    RETURN NEW;
                END;
                $luam$;

                CREATE TRIGGER TR_profile_lifecycle_revision_nonnegative
                BEFORE INSERT OR UPDATE OF lifecycle_revision ON profile
                FOR EACH ROW EXECUTE FUNCTION luam_reject_negative_profile_lifecycle_revision();

                CREATE FUNCTION luam_reject_deep_cryo_operation_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $luam$
                BEGIN
                    RAISE EXCEPTION 'LuaM deep-cryo operation journal is append-only';
                END;
                $luam$;

                CREATE TRIGGER TR_luam_cryo_operation_no_update
                BEFORE UPDATE ON luam_deep_cryo_operation
                FOR EACH ROW EXECUTE FUNCTION luam_reject_deep_cryo_operation_mutation();

                CREATE TRIGGER TR_luam_cryo_operation_no_delete
                BEFORE DELETE ON luam_deep_cryo_operation
                FOR EACH ROW EXECUTE FUNCTION luam_reject_deep_cryo_operation_mutation();

                CREATE FUNCTION luam_reject_deep_cryo_snapshot_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $luam$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'LuaM deep-cryo snapshots cannot be deleted';
                    END IF;

                    IF ROW(
                        OLD.player_user_id,
                        OLD.preference_id,
                        OLD.profile_id,
                        OLD.slot,
                        OLD.source_round_id,
                        OLD.format_version,
                        OLD.payload,
                        OLD.payload_hash,
                        OLD.payload_size_bytes,
                        OLD.entity_count,
                        OLD.source_build_version,
                        OLD.prototype_manifest_hash,
                        OLD.stored_at_utc)
                       IS DISTINCT FROM ROW(
                        NEW.player_user_id,
                        NEW.preference_id,
                        NEW.profile_id,
                        NEW.slot,
                        NEW.source_round_id,
                        NEW.format_version,
                        NEW.payload,
                        NEW.payload_hash,
                        NEW.payload_size_bytes,
                        NEW.entity_count,
                        NEW.source_build_version,
                        NEW.prototype_manifest_hash,
                        NEW.stored_at_utc) THEN
                        RAISE EXCEPTION 'LuaM deep-cryo snapshot identity and payload are immutable';
                    END IF;

                    RETURN NEW;
                END;
                $luam$;

                CREATE TRIGGER TR_luam_cryo_snapshot_immutable_payload
                BEFORE UPDATE ON luam_deep_cryo_snapshot
                FOR EACH ROW EXECUTE FUNCTION luam_reject_deep_cryo_snapshot_mutation();

                CREATE TRIGGER TR_luam_cryo_snapshot_no_delete
                BEFORE DELETE ON luam_deep_cryo_snapshot
                FOR EACH ROW EXECUTE FUNCTION luam_reject_deep_cryo_snapshot_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $luam$
                BEGIN
                    IF EXISTS (SELECT 1 FROM luam_deep_cryo_snapshot)
                       OR EXISTS (SELECT 1 FROM luam_character_presence_lease)
                       OR EXISTS (SELECT 1 FROM luam_deep_cryo_operation) THEN
                        RAISE EXCEPTION 'Cannot downgrade LuaM deep-cryo persistence while durable rows exist.';
                    END IF;
                END;
                $luam$;
                """);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_operation_no_update ON luam_deep_cryo_operation;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_operation_no_delete ON luam_deep_cryo_operation;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_snapshot_immutable_payload ON luam_deep_cryo_snapshot;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_luam_cryo_snapshot_no_delete ON luam_deep_cryo_snapshot;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_profile_lifecycle_revision_nonnegative ON profile;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS luam_reject_deep_cryo_operation_mutation();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS luam_reject_deep_cryo_snapshot_mutation();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS luam_reject_negative_profile_lifecycle_revision();");

            migrationBuilder.DropTable(
                name: "luam_character_presence_lease");

            migrationBuilder.DropTable(
                name: "luam_deep_cryo_operation");

            migrationBuilder.DropTable(
                name: "luam_deep_cryo_snapshot");

            migrationBuilder.DropColumn(
                name: "lifecycle_revision",
                table: "profile");
        }
    }
}
