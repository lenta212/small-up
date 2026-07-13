using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class PreserveCharacterProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "slot",
                table: "profile",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "profile",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_archived",
                table: "profile",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_profile_archive_state",
                table: "profile",
                sql: "(is_archived = TRUE AND slot IS NULL AND archived_at IS NOT NULL) OR (is_archived = FALSE AND slot IS NOT NULL AND archived_at IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The legacy schema cannot represent preserved profiles without a slot.
            // Refuse a destructive downgrade instead of deleting archived identities.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM profile
                        WHERE is_archived = TRUE OR slot IS NULL OR archived_at IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade PreserveCharacterProfiles while archived profiles exist.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_profile_archive_state",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "is_archived",
                table: "profile");

            migrationBuilder.AlterColumn<int>(
                name: "slot",
                table: "profile",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
