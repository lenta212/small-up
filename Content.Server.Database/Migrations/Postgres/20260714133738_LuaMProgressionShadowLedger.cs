using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
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
                    shift_period_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sealed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    total_career_xp = table.Column<long>(type: "bigint", nullable: false),
                    credited_shift_count = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    shift_period_id = table.Column<long>(type: "bigint", nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: false),
                    attached_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    shift_period_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    preference_id = table.Column<int>(type: "integer", nullable: false),
                    is_career_focus = table.Column<bool>(type: "boolean", nullable: false),
                    preliminary_career_xp = table.Column<int>(type: "integer", nullable: false),
                    result_career_xp = table.Column<int>(type: "integer", nullable: false),
                    active_minutes = table.Column<int>(type: "integer", nullable: false),
                    distinct_result_categories = table.Column<int>(type: "integer", nullable: false),
                    final_career_xp = table.Column<int>(type: "integer", nullable: false),
                    is_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    is_credited = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sealed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_luam_career_shift_participation", x => new { x.shift_period_id, x.profile_id });
                    table.CheckConstraint("CK_luam_participation_credit", "NOT is_credited OR is_eligible");
                    table.CheckConstraint("CK_luam_participation_final", "final_career_xp >= 0 AND final_career_xp <= 100");
                    table.CheckConstraint("CK_luam_participation_revision", "revision >= 0");
                    table.CheckConstraint("CK_luam_participation_values", "preliminary_career_xp >= 0 AND result_career_xp >= 0 AND active_minutes >= 0 AND distinct_result_categories >= 0");
                    table.ForeignKey(
                        name: "FK_luam_career_shift_participation_luam_campaign_shift_shift_p~",
                        column: x => x.shift_period_id,
                        principalTable: "luam_campaign_shift",
                        principalColumn: "shift_period_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_career_shift_participation_luam_character_career_profi~",
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
                    lua_m_career_xp_ledger_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    shift_period_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    currency = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    source_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    award_code = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operation_identity_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reverses_ledger_id = table.Column<long>(type: "bigint", nullable: true),
                    ruleset_version = table.Column<int>(type: "integer", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                        name: "FK_luam_career_xp_ledger_luam_career_shift_participation_lua_m~",
                        columns: x => new { x.shift_period_id, x.profile_id },
                        principalTable: "luam_career_shift_participation",
                        principalColumns: new[] { "shift_period_id", "profile_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_luam_career_xp_ledger_luam_career_xp_ledger_lua_m_career_xp~",
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
                CREATE FUNCTION luam_reject_career_ledger_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $luam$
                BEGIN
                    RAISE EXCEPTION 'LuaM career ledger is append-only';
                END;
                $luam$;

                CREATE TRIGGER TR_luam_ledger_append_only
                BEFORE UPDATE OR DELETE ON luam_career_xp_ledger
                FOR EACH ROW EXECUTE FUNCTION luam_reject_career_ledger_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $luam$
                BEGIN
                    IF EXISTS (SELECT 1 FROM luam_campaign_shift)
                       OR EXISTS (SELECT 1 FROM luam_character_career)
                       OR EXISTS (SELECT 1 FROM luam_career_xp_ledger) THEN
                        RAISE EXCEPTION 'Cannot downgrade LuaM progression while durable rows exist.';
                    END IF;
                END
                $luam$;
                """);

            migrationBuilder.Sql("DROP FUNCTION luam_reject_career_ledger_mutation() CASCADE;");

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
