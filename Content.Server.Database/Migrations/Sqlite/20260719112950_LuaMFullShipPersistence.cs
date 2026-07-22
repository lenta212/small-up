using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class LuaMFullShipPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "luam_ship_snapshot",
                columns: table => new
                {
                    ship_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_preference_id = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    vessel_prototype_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ship_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ship_name_suffix = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    purchase_price = table.Column<int>(type: "INTEGER", nullable: false),
                    purchased_with_voucher = table.Column<bool>(type: "INTEGER", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    format_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    payload_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    payload_size_bytes = table.Column<int>(type: "INTEGER", nullable: false),
                    entity_count = table.Column<int>(type: "INTEGER", nullable: false),
                    source_build_version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    prototype_manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    source_round_id = table.Column<int>(type: "INTEGER", nullable: false),
                    last_restore_round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    stored_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_restored_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    quarantined_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    quarantine_reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    retired_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    retirement_reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    retirement_operation_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_ship_snapshot", x => x.ship_id);
                    table.CheckConstraint("CK_luam_ship_snapshot_dates", "created_at_utc <= stored_at_utc AND stored_at_utc <= updated_at_utc");
                    table.CheckConstraint("CK_luam_ship_snapshot_hashes", "length(payload_hash) = 64 AND length(prototype_manifest_hash) = 64");
                    table.CheckConstraint("CK_luam_ship_snapshot_owner", "owner_preference_id > 0");
                    table.CheckConstraint("CK_luam_ship_snapshot_payload", "payload_size_bytes > 0 AND entity_count > 0 AND length(payload) = payload_size_bytes");
                    table.CheckConstraint("CK_luam_ship_snapshot_purchase", "purchase_price >= 0");
                    table.CheckConstraint("CK_luam_ship_snapshot_quarantine", "(status = 3 AND quarantined_at_utc IS NOT NULL AND quarantine_reason IS NOT NULL) OR (status <> 3 AND quarantined_at_utc IS NULL AND quarantine_reason IS NULL)");
                    table.CheckConstraint("CK_luam_ship_snapshot_retirement", "(status = 4 AND retired_at_utc IS NOT NULL AND retirement_reason IS NOT NULL AND retirement_operation_id IS NOT NULL) OR (status <> 4 AND retired_at_utc IS NULL AND retirement_reason IS NULL AND retirement_operation_id IS NULL)");
                    table.CheckConstraint("CK_luam_ship_snapshot_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_ship_snapshot_status", "status >= 0 AND status <= 4");
                    table.CheckConstraint("CK_luam_ship_snapshot_versions", "schema_version > 0 AND format_version > 0 AND source_round_id >= 0");
                    table.ForeignKey(
                        name: "FK_luam_ship_snapshot_preference_preference_id",
                        column: x => x.owner_preference_id,
                        principalTable: "preference",
                        principalColumn: "preference_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_ship_presence_lease",
                columns: table => new
                {
                    ship_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    lease_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    server_instance_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    round_id = table.Column<int>(type: "INTEGER", nullable: false),
                    acquired_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    renewed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_ship_presence_lease", x => x.ship_id);
                    table.CheckConstraint("CK_luam_ship_lease_dates", "acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc");
                    table.CheckConstraint("CK_luam_ship_lease_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_ship_lease_round", "round_id >= 0");
                    table.ForeignKey(
                        name: "FK_luam_ship_presence_lease_luam_ship_snapshot_lua_m_ship_snapshot_ship_id",
                        column: x => x.ship_id,
                        principalTable: "luam_ship_snapshot",
                        principalColumn: "ship_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_luam_ship_lease_expires",
                table: "luam_ship_presence_lease",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "UX_luam_ship_lease_token",
                table: "luam_ship_presence_lease",
                column: "lease_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_ship_snapshot_owner_preference_id",
                table: "luam_ship_snapshot",
                column: "owner_preference_id");

            migrationBuilder.CreateIndex(
                name: "IX_luam_ship_snapshot_owner_status",
                table: "luam_ship_snapshot",
                columns: new[] { "owner_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_ship_snapshot_updated",
                table: "luam_ship_snapshot",
                column: "updated_at_utc");

            migrationBuilder.CreateIndex(
                name: "UX_luam_ship_retirement_operation",
                table: "luam_ship_snapshot",
                column: "retirement_operation_id",
                unique: true,
                filter: "retirement_operation_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "luam_ship_presence_lease");

            migrationBuilder.DropTable(
                name: "luam_ship_snapshot");
        }
    }
}
