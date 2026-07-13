using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DurablePdaBankTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "character_bank_transfer_journal",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sender_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    recipient_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    amount = table.Column<int>(type: "INTEGER", nullable: false),
                    sender_balance_before = table.Column<int>(type: "INTEGER", nullable: false),
                    sender_balance_after = table.Column<int>(type: "INTEGER", nullable: false),
                    recipient_balance_before = table.Column<int>(type: "INTEGER", nullable: false),
                    recipient_balance_after = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    acknowledged_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_bank_transfer_journal", x => x.operation_id);
                });

            migrationBuilder.CreateTable(
                name: "pda_bank_accounts",
                columns: table => new
                {
                    bank_id = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    last_user_name = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pda_bank_accounts", x => x.bank_id);
                    table.ForeignKey(
                        name: "FK_pda_bank_accounts_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_character_bank_transfer_journal_created_at",
                table: "character_bank_transfer_journal",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_character_bank_transfer_journal_recipient_profile_id",
                table: "character_bank_transfer_journal",
                column: "recipient_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_character_bank_transfer_journal_sender_profile_id_acknowledged_at",
                table: "character_bank_transfer_journal",
                columns: new[] { "sender_profile_id", "acknowledged_at" });

            migrationBuilder.CreateIndex(
                name: "IX_pda_bank_accounts_profile_id",
                table: "pda_bank_accounts",
                column: "profile_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_bank_transfer_journal");

            migrationBuilder.DropTable(
                name: "pda_bank_accounts");
        }
    }
}
