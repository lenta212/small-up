using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class LuaMAtomicMonoCoinsTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monocoins_transfer_journal",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    sender_balance_before = table.Column<long>(type: "bigint", nullable: false),
                    sender_balance_after = table.Column<long>(type: "bigint", nullable: false),
                    recipient_balance_before = table.Column<long>(type: "bigint", nullable: false),
                    recipient_balance_after = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monocoins_transfer_journal", x => x.operation_id);
                    table.CheckConstraint("CK_monocoins_transfer_journal_amount", "amount > 0");
                    table.CheckConstraint("CK_monocoins_transfer_journal_balances", "sender_balance_before >= 0 AND sender_balance_after >= 0 AND recipient_balance_before >= 0 AND recipient_balance_after >= 0");
                    table.CheckConstraint("CK_monocoins_transfer_journal_conservation", "sender_balance_before - sender_balance_after = amount AND recipient_balance_after - recipient_balance_before = amount");
                    table.CheckConstraint("CK_monocoins_transfer_journal_distinct_accounts", "sender_user_id <> recipient_user_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_monocoins_transfer_journal_created_at",
                table: "monocoins_transfer_journal",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_monocoins_transfer_journal_recipient_user_id",
                table: "monocoins_transfer_journal",
                column: "recipient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_monocoins_transfer_journal_sender_user_id",
                table: "monocoins_transfer_journal",
                column: "sender_user_id",
                unique: true,
                filter: "acknowledged_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $monocoins$
                BEGIN
                    IF EXISTS (SELECT 1 FROM monocoins_transfer_journal) THEN
                        RAISE EXCEPTION 'Cannot downgrade atomic MonoCoins transfers while journal rows exist.';
                    END IF;
                END
                $monocoins$;
                """);

            migrationBuilder.DropTable(
                name: "monocoins_transfer_journal");
        }
    }
}
