using Content.Server.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20260727090000_LuaMShipFleetOwnerIndex")]
public partial class LuaMShipFleetOwnerIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Fleet storage intentionally supports multiple non-retired hulls per owner.
        migrationBuilder.Sql("DROP INDEX IF EXISTS UX_luam_ship_snapshot_active_owner;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The previous canonical SQLite schema did not contain this index.
        // Up also reconciles databases that acquired it from a divergent build,
        // but downgrading a canonical database must not introduce new uniqueness.
    }
}
