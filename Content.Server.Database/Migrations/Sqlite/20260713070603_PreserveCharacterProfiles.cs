using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
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
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "profile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_archived",
                table: "profile",
                type: "INTEGER",
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
                CREATE TEMP TABLE __profile_archive_downgrade_guard (
                    value INTEGER NOT NULL CHECK (value = 0)
                );
                INSERT INTO __profile_archive_downgrade_guard (value)
                SELECT 1
                WHERE EXISTS (
                    SELECT 1
                    FROM profile
                    WHERE is_archived = 1 OR slot IS NULL OR archived_at IS NOT NULL
                );
                DROP TABLE __profile_archive_downgrade_guard;
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
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
