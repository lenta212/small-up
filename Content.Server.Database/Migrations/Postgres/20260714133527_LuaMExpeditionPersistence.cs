using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class LuaMExpeditionPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "luam_expedition_manifest",
                columns: table => new
                {
                    lua_m_expedition_manifests_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    campaign_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expedition_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    seed_bits = table.Column<long>(type: "bigint", nullable: false),
                    generator_version = table.Column<int>(type: "integer", nullable: false),
                    plan_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    min_x = table.Column<int>(type: "integer", nullable: false),
                    min_y = table.Column<int>(type: "integer", nullable: false),
                    max_x = table.Column<int>(type: "integer", nullable: false),
                    max_y = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    preservation_policy = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_manifest", x => x.lua_m_expedition_manifests_id);
                    table.CheckConstraint("CK_luam_exp_manifest_bounds", "min_x <= max_x AND min_y <= max_y");
                    table.CheckConstraint("CK_luam_exp_manifest_policy", "preservation_policy >= 0 AND preservation_policy <= 2");
                    table.CheckConstraint("CK_luam_exp_manifest_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_exp_manifest_status", "status >= 0 AND status <= 6");
                    table.CheckConstraint("CK_luam_exp_manifest_version", "generator_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "luam_expedition_checkpoint",
                columns: table => new
                {
                    lua_m_expedition_checkpoints_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manifest_id = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_identity_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    state_payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    committed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_checkpoint", x => x.lua_m_expedition_checkpoints_id);
                    table.CheckConstraint("CK_luam_exp_checkpoint_identity", "length(operation_identity_key) = 64");
                    table.CheckConstraint("CK_luam_exp_checkpoint_status", "status >= 0 AND status <= 1");
                    table.CheckConstraint("CK_luam_exp_checkpoint_version", "format_version > 0");
                    table.ForeignKey(
                        name: "FK_luam_expedition_checkpoint_luam_expedition_manifest_lua_m_e~",
                        column: x => x.manifest_id,
                        principalTable: "luam_expedition_manifest",
                        principalColumn: "lua_m_expedition_manifests_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_expedition_region",
                columns: table => new
                {
                    lua_m_expedition_regions_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manifest_id = table.Column<long>(type: "bigint", nullable: false),
                    region_x = table.Column<int>(type: "integer", nullable: false),
                    region_y = table.Column<int>(type: "integer", nullable: false),
                    min_x = table.Column<int>(type: "integer", nullable: false),
                    min_y = table.Column<int>(type: "integer", nullable: false),
                    max_x = table.Column<int>(type: "integer", nullable: false),
                    max_y = table.Column<int>(type: "integer", nullable: false),
                    biome = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scenario_tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    danger_budget = table.Column<int>(type: "integer", nullable: false),
                    discovered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_depleted = table.Column<bool>(type: "boolean", nullable: false),
                    edge_format_version = table.Column<int>(type: "integer", nullable: false),
                    edge_payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    edge_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    quarantined_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_region", x => x.lua_m_expedition_regions_id);
                    table.CheckConstraint("CK_luam_exp_region_bounds", "min_x <= max_x AND min_y <= max_y");
                    table.CheckConstraint("CK_luam_exp_region_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_exp_region_version", "edge_format_version > 0");
                    table.ForeignKey(
                        name: "FK_luam_expedition_region_luam_expedition_manifest_lua_m_exped~",
                        column: x => x.manifest_id,
                        principalTable: "luam_expedition_manifest",
                        principalColumn: "lua_m_expedition_manifests_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_expedition_site",
                columns: table => new
                {
                    lua_m_expedition_sites_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manifest_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    prototype_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    unique_scope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    position_x = table.Column<int>(type: "integer", nullable: false),
                    position_y = table.Column<int>(type: "integer", nullable: false),
                    min_x = table.Column<int>(type: "integer", nullable: false),
                    min_y = table.Column<int>(type: "integer", nullable: false),
                    max_x = table.Column<int>(type: "integer", nullable: false),
                    max_y = table.Column<int>(type: "integer", nullable: false),
                    state_format_version = table.Column<int>(type: "integer", nullable: false),
                    state_payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    state_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    discovered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_site", x => x.lua_m_expedition_sites_id);
                    table.CheckConstraint("CK_luam_exp_site_bounds", "min_x <= max_x AND min_y <= max_y");
                    table.CheckConstraint("CK_luam_exp_site_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_exp_site_version", "state_format_version > 0");
                    table.ForeignKey(
                        name: "FK_luam_expedition_site_luam_expedition_manifest_lua_m_expedit~",
                        column: x => x.manifest_id,
                        principalTable: "luam_expedition_manifest",
                        principalColumn: "lua_m_expedition_manifests_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_expedition_delta",
                columns: table => new
                {
                    lua_m_expedition_deltas_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    region_id = table.Column<long>(type: "bigint", nullable: false),
                    region_revision = table.Column<long>(type: "bigint", nullable: false),
                    source_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_delta", x => x.lua_m_expedition_deltas_id);
                    table.CheckConstraint("CK_luam_exp_delta_revision", "region_revision > 0");
                    table.CheckConstraint("CK_luam_exp_delta_version", "format_version > 0");
                    table.ForeignKey(
                        name: "FK_luam_expedition_delta_luam_expedition_region_lua_m_expediti~",
                        column: x => x.region_id,
                        principalTable: "luam_expedition_region",
                        principalColumn: "lua_m_expedition_regions_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_expedition_delta_profile_profile_id",
                        column: x => x.actor_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_expedition_entity_snapshot",
                columns: table => new
                {
                    lua_m_expedition_entity_snapshots_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    region_id = table.Column<long>(type: "bigint", nullable: false),
                    stable_entity_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    region_revision = table.Column<long>(type: "bigint", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_entity_snapshot", x => x.lua_m_expedition_entity_snapshots_id);
                    table.CheckConstraint("CK_luam_exp_snapshot_key", "length(idempotency_key) = 64");
                    table.CheckConstraint("CK_luam_exp_snapshot_revision", "region_revision > 0");
                    table.CheckConstraint("CK_luam_exp_snapshot_status", "status >= 0 AND status <= 1");
                    table.CheckConstraint("CK_luam_exp_snapshot_version", "format_version > 0");
                    table.ForeignKey(
                        name: "FK_luam_expedition_entity_snapshot_luam_expedition_region_lua_~",
                        column: x => x.region_id,
                        principalTable: "luam_expedition_region",
                        principalColumn: "lua_m_expedition_regions_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_expedition_entity_snapshot_profile_profile_id",
                        column: x => x.actor_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_expedition_tombstone",
                columns: table => new
                {
                    lua_m_expedition_tombstones_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    region_id = table.Column<long>(type: "bigint", nullable: false),
                    site_row_id = table.Column<long>(type: "bigint", nullable: true),
                    stable_object_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    region_revision = table.Column<long>(type: "bigint", nullable: false),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_expedition_tombstone", x => x.lua_m_expedition_tombstones_id);
                    table.CheckConstraint("CK_luam_exp_tombstone_revision", "region_revision > 0");
                    table.ForeignKey(
                        name: "FK_luam_expedition_tombstone_luam_expedition_region_lua_m_expe~",
                        column: x => x.region_id,
                        principalTable: "luam_expedition_region",
                        principalColumn: "lua_m_expedition_regions_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_expedition_tombstone_luam_expedition_site_lua_m_expedi~",
                        column: x => x.site_row_id,
                        principalTable: "luam_expedition_site",
                        principalColumn: "lua_m_expedition_sites_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_expedition_tombstone_profile_profile_id",
                        column: x => x.actor_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_luam_exp_checkpoint_committed",
                table: "luam_expedition_checkpoint",
                columns: new[] { "manifest_id", "committed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_checkpoint_operation",
                table: "luam_expedition_checkpoint",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_expedition_delta_actor_profile_id",
                table: "luam_expedition_delta",
                column: "actor_profile_id");

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_delta_revision",
                table: "luam_expedition_delta",
                columns: new[] { "region_id", "region_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_delta_source",
                table: "luam_expedition_delta",
                columns: new[] { "region_id", "source_instance_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_expedition_entity_snapshot_actor_profile_id",
                table: "luam_expedition_entity_snapshot",
                column: "actor_profile_id");

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_snapshot_entity",
                table: "luam_expedition_entity_snapshot",
                columns: new[] { "region_id", "stable_entity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_snapshot_idempotency",
                table: "luam_expedition_entity_snapshot",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_exp_manifest_status_updated",
                table: "luam_expedition_manifest",
                columns: new[] { "status", "updated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_manifest_identity",
                table: "luam_expedition_manifest",
                columns: new[] { "campaign_id", "expedition_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_exp_region_discovered",
                table: "luam_expedition_region",
                columns: new[] { "manifest_id", "discovered_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_exp_region_revision",
                table: "luam_expedition_region",
                columns: new[] { "manifest_id", "revision" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_region_coordinates",
                table: "luam_expedition_region",
                columns: new[] { "manifest_id", "region_x", "region_y" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_exp_site_kind",
                table: "luam_expedition_site",
                columns: new[] { "manifest_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_site_identity",
                table: "luam_expedition_site",
                columns: new[] { "manifest_id", "site_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_site_scope_proto",
                table: "luam_expedition_site",
                columns: new[] { "manifest_id", "unique_scope", "prototype_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_exp_tombstone_source",
                table: "luam_expedition_tombstone",
                columns: new[] { "region_id", "source_instance_id" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_expedition_tombstone_actor_profile_id",
                table: "luam_expedition_tombstone",
                column: "actor_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_luam_expedition_tombstone_site_row_id",
                table: "luam_expedition_tombstone",
                column: "site_row_id");

            migrationBuilder.CreateIndex(
                name: "UX_luam_exp_tombstone_object",
                table: "luam_expedition_tombstone",
                columns: new[] { "region_id", "stable_object_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $luam$
                BEGIN
                    IF EXISTS (SELECT 1 FROM luam_expedition_manifest) THEN
                        RAISE EXCEPTION 'Cannot downgrade LuaM expedition persistence while durable rows exist.';
                    END IF;
                END
                $luam$;
                """);

            migrationBuilder.DropTable(
                name: "luam_expedition_checkpoint");

            migrationBuilder.DropTable(
                name: "luam_expedition_delta");

            migrationBuilder.DropTable(
                name: "luam_expedition_entity_snapshot");

            migrationBuilder.DropTable(
                name: "luam_expedition_tombstone");

            migrationBuilder.DropTable(
                name: "luam_expedition_region");

            migrationBuilder.DropTable(
                name: "luam_expedition_site");

            migrationBuilder.DropTable(
                name: "luam_expedition_manifest");
        }
    }
}
