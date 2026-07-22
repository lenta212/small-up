using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class LuaMProgressionShadowLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "luam_campaign_shift",
                columns: table => new
                {
                    shift_period_id = table.Column<long>(type: "INTEGER", nullable: false),
                    starts_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    sealed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_campaign_shift", x => x.shift_period_id);
                    table.CheckConstraint("CK_luam_shift_dates", "starts_at_utc < ends_at_utc");
                    table.CheckConstraint("CK_luam_shift_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_shift_status", "status >= 0 AND status <= 3");
                });

            migrationBuilder.CreateTable(
                name: "luam_character_career",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    total_career_xp = table.Column<long>(type: "INTEGER", nullable: false),
                    credited_shift_count = table.Column<int>(type: "INTEGER", nullable: false),
                    level = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_character_career", x => x.profile_id);
                    table.CheckConstraint("CK_luam_career_level", "level >= 0 AND level <= 10");
                    table.CheckConstraint("CK_luam_career_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_career_status", "status >= 0 AND status <= 6");
                    table.CheckConstraint("CK_luam_career_totals", "total_career_xp >= 0 AND credited_shift_count >= 0");
                    table.ForeignKey(
                        name: "FK_luam_character_career_profile_profile_id1",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_campaign_shift_run",
                columns: table => new
                {
                    shift_period_id = table.Column<long>(type: "INTEGER", nullable: false),
                    round_id = table.Column<int>(type: "INTEGER", nullable: false),
                    attached_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_campaign_shift_run", x => new { x.shift_period_id, x.round_id });
                    table.ForeignKey(
                        name: "FK_luam_campaign_shift_run_luam_campaign_shift_shift_period_id",
                        column: x => x.shift_period_id,
                        principalTable: "luam_campaign_shift",
                        principalColumn: "shift_period_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_career_shift_participation",
                columns: table => new
                {
                    shift_period_id = table.Column<long>(type: "INTEGER", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    preference_id = table.Column<int>(type: "INTEGER", nullable: false),
                    is_career_focus = table.Column<bool>(type: "INTEGER", nullable: false),
                    preliminary_career_xp = table.Column<int>(type: "INTEGER", nullable: false),
                    result_career_xp = table.Column<int>(type: "INTEGER", nullable: false),
                    active_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    distinct_result_categories = table.Column<int>(type: "INTEGER", nullable: false),
                    final_career_xp = table.Column<int>(type: "INTEGER", nullable: false),
                    is_eligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_credited = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    sealed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_career_shift_participation", x => new { x.shift_period_id, x.profile_id });
                    table.CheckConstraint("CK_luam_participation_credit", "NOT is_credited OR is_eligible");
                    table.CheckConstraint("CK_luam_participation_final", "final_career_xp >= 0 AND final_career_xp <= 100");
                    table.CheckConstraint("CK_luam_participation_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_participation_values", "preliminary_career_xp >= 0 AND result_career_xp >= 0 AND active_minutes >= 0 AND distinct_result_categories >= 0");
                    table.ForeignKey(
                        name: "FK_luam_career_shift_participation_luam_campaign_shift_shift_period_id",
                        column: x => x.shift_period_id,
                        principalTable: "luam_campaign_shift",
                        principalColumn: "shift_period_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_career_shift_participation_luam_character_career_profile_id",
                        column: x => x.profile_id,
                        principalTable: "luam_character_career",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_career_shift_participation_preference_preference_id",
                        column: x => x.preference_id,
                        principalTable: "preference",
                        principalColumn: "preference_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "luam_career_xp_ledger",
                columns: table => new
                {
                    lua_m_career_xp_ledger_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    shift_period_id = table.Column<long>(type: "INTEGER", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    currency = table.Column<int>(type: "INTEGER", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    amount = table.Column<int>(type: "INTEGER", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    source_instance_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    award_code = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    operation_identity_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    reverses_ledger_id = table.Column<long>(type: "INTEGER", nullable: true),
                    ruleset_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_career_xp_ledger", x => x.lua_m_career_xp_ledger_id);
                    table.CheckConstraint("CK_luam_ledger_amount", "amount <> 0");
                    table.CheckConstraint("CK_luam_ledger_currency", "currency >= 0 AND currency <= 2");
                    table.CheckConstraint("CK_luam_ledger_key", "length(idempotency_key) = 64");
                    table.CheckConstraint("CK_luam_ledger_operation_identity", "length(operation_identity_key) = 64");
                    table.CheckConstraint("CK_luam_ledger_reversal", "reverses_ledger_id IS NULL OR reverses_ledger_id <> lua_m_career_xp_ledger_id");
                    table.CheckConstraint("CK_luam_ledger_ruleset", "ruleset_version > 0");
                    table.ForeignKey(
                        name: "FK_luam_career_xp_ledger_luam_career_shift_participation_lua_m_career_shift_participation_shift_period_id_lua_m_career_shift_participation_profile_id",
                        columns: x => new { x.shift_period_id, x.profile_id },
                        principalTable: "luam_career_shift_participation",
                        principalColumns: new[] { "shift_period_id", "profile_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_career_xp_ledger_luam_career_xp_ledger_lua_m_career_xp_ledger_id",
                        column: x => x.reverses_ledger_id,
                        principalTable: "luam_career_xp_ledger",
                        principalColumn: "lua_m_career_xp_ledger_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_luam_shift_start",
                table: "luam_campaign_shift",
                column: "starts_at_utc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_luam_shift_run_round",
                table: "luam_campaign_shift_run",
                column: "round_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_luam_career_shift_participation_preference_id",
                table: "luam_career_shift_participation",
                column: "preference_id");

            migrationBuilder.CreateIndex(
                name: "IX_luam_participation_eligible",
                table: "luam_career_shift_participation",
                columns: new[] { "shift_period_id", "is_eligible" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_participation_profile",
                table: "luam_career_shift_participation",
                columns: new[] { "profile_id", "shift_period_id" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_participation_focus",
                table: "luam_career_shift_participation",
                columns: new[] { "shift_period_id", "preference_id" },
                unique: true,
                filter: "is_career_focus = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_luam_ledger_profile_created",
                table: "luam_career_xp_ledger",
                columns: new[] { "profile_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_luam_ledger_shift_profile_currency",
                table: "luam_career_xp_ledger",
                columns: new[] { "shift_period_id", "profile_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "UX_luam_ledger_idempotency",
                table: "luam_career_xp_ledger",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_luam_ledger_reversal",
                table: "luam_career_xp_ledger",
                column: "reverses_ledger_id",
                unique: true);

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_luam_ledger_no_update
                BEFORE UPDATE ON luam_career_xp_ledger
                BEGIN
                    SELECT RAISE(ABORT, 'LuaM career ledger is append-only');
                END;

                CREATE TRIGGER TR_luam_ledger_no_delete
                BEFORE DELETE ON luam_career_xp_ledger
                BEGIN
                    SELECT RAISE(ABORT, 'LuaM career ledger is append-only');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __luam_progression_downgrade_guard (
                    value INTEGER NOT NULL CHECK (value = 0)
                );
                INSERT INTO __luam_progression_downgrade_guard (value)
                SELECT 1
                WHERE EXISTS (SELECT 1 FROM luam_campaign_shift)
                   OR EXISTS (SELECT 1 FROM luam_character_career)
                   OR EXISTS (SELECT 1 FROM luam_career_xp_ledger);
                DROP TABLE __luam_progression_downgrade_guard;
                """);

            migrationBuilder.DropTable(
                name: "luam_campaign_shift_run");

            migrationBuilder.DropTable(
                name: "luam_career_xp_ledger");

            migrationBuilder.DropTable(
                name: "luam_career_shift_participation");

            migrationBuilder.DropTable(
                name: "luam_campaign_shift");

            migrationBuilder.DropTable(
                name: "luam_character_career");
        }
    }
}
