using Content.Server.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20260722150000_LuaMCharacterPresenceAuthority")]
public partial class LuaMCharacterPresenceAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE __luam_presence_epoch_guard (
                value INTEGER NOT NULL CHECK (value = 0)
            );
            INSERT INTO __luam_presence_epoch_guard(value)
            SELECT 1
            FROM luam_character_presence_lease AS lease
            JOIN luam_deep_cryo_snapshot AS snapshot
              ON snapshot.lua_m_deep_cryo_snapshots_id = lease.snapshot_id
             AND snapshot.profile_id = lease.profile_id
            JOIN profile ON profile.profile_id = lease.profile_id
            WHERE (snapshot.status = 2 AND (
                    SELECT COUNT(*) FROM luam_deep_cryo_operation AS operation
                    WHERE operation.snapshot_id = snapshot.lua_m_deep_cryo_snapshots_id
                      AND operation.profile_id = snapshot.profile_id
                      AND operation.kind = 2
                      AND operation.result_status = 2
                      AND operation.result_revision = snapshot.revision
                      AND operation.lease_id = lease.lease_id) <> 1)
               OR CASE
                    WHEN snapshot.status = 1 THEN 3
                    WHEN snapshot.status = 2 AND EXISTS (
                        SELECT 1 FROM luam_deep_cryo_operation AS operation
                        WHERE operation.snapshot_id = snapshot.lua_m_deep_cryo_snapshots_id
                          AND operation.profile_id = snapshot.profile_id
                          AND operation.kind = 2
                          AND operation.result_status = 2
                          AND operation.result_revision = snapshot.revision
                          AND operation.lease_id = lease.lease_id
                          AND operation.reason IS NULL) THEN 2
                    WHEN snapshot.status = 2 AND EXISTS (
                        SELECT 1 FROM luam_deep_cryo_operation AS operation
                        WHERE operation.snapshot_id = snapshot.lua_m_deep_cryo_snapshots_id
                          AND operation.profile_id = snapshot.profile_id
                          AND operation.kind = 2
                          AND operation.result_status = 2
                          AND operation.result_revision = snapshot.revision
                          AND operation.lease_id = lease.lease_id
                          AND operation.reason = 'publication-authorized-v1') THEN 1
                    ELSE NULL
                  END IS NULL
               OR profile.lifecycle_revision > 9223372036854775807 - CASE
                    WHEN snapshot.status = 1 THEN 3
                    WHEN snapshot.status = 2 AND EXISTS (
                        SELECT 1 FROM luam_deep_cryo_operation AS operation
                        WHERE operation.snapshot_id = snapshot.lua_m_deep_cryo_snapshots_id
                          AND operation.profile_id = snapshot.profile_id
                          AND operation.kind = 2
                          AND operation.result_status = 2
                          AND operation.result_revision = snapshot.revision
                          AND operation.lease_id = lease.lease_id
                          AND operation.reason IS NULL) THEN 2
                    ELSE 1
                  END
            LIMIT 1;
            DROP TABLE __luam_presence_epoch_guard;

            CREATE TABLE __luam_character_presence_lease_new (
                profile_id INTEGER NOT NULL,
                snapshot_id INTEGER NULL,
                lease_id TEXT NOT NULL,
                phase INTEGER NOT NULL,
                server_instance_id TEXT NOT NULL,
                round_id INTEGER NOT NULL,
                acquired_at_utc TEXT NOT NULL,
                renewed_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                revision INTEGER NOT NULL,
                authority_lifecycle_revision INTEGER NOT NULL,
                last_renewal_operation_id TEXT NULL,
                last_renewal_operation_identity_key TEXT NULL,
                CONSTRAINT PK_luam_character_presence_lease PRIMARY KEY (profile_id),
                CONSTRAINT CK_luam_cryo_lease_dates CHECK (
                    acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc),
                CONSTRAINT CK_luam_cryo_lease_phase CHECK (phase >= 0 AND phase <= 2),
                CONSTRAINT CK_luam_cryo_lease_revision CHECK (
                    revision >= 0 AND authority_lifecycle_revision >= 0),
                CONSTRAINT CK_luam_cryo_lease_round CHECK (round_id >= 0),
                CONSTRAINT CK_luam_cryo_lease_snapshot_phase CHECK (
                    (phase = 0 AND snapshot_id IS NOT NULL) OR
                    (phase = 1 AND snapshot_id IS NULL) OR phase = 2),
                CONSTRAINT CK_luam_cryo_lease_last_renewal CHECK (
                    (last_renewal_operation_id IS NULL AND
                     last_renewal_operation_identity_key IS NULL) OR
                    (last_renewal_operation_id IS NOT NULL AND
                     last_renewal_operation_identity_key IS NOT NULL AND
                     length(last_renewal_operation_identity_key) = 64)),
                CONSTRAINT FK_luam_character_presence_lease_profile_profile_id1
                    FOREIGN KEY (profile_id) REFERENCES profile(profile_id) ON DELETE RESTRICT,
                CONSTRAINT FK_luam_character_presence_lease_luam_deep_cryo_snapshot_lua_m_deep_cryo_snapshot_id
                    FOREIGN KEY (snapshot_id, profile_id)
                    REFERENCES luam_deep_cryo_snapshot(lua_m_deep_cryo_snapshots_id, profile_id)
                    ON DELETE RESTRICT
            );

            INSERT INTO __luam_character_presence_lease_new (
                profile_id,
                snapshot_id,
                lease_id,
                phase,
                server_instance_id,
                round_id,
                acquired_at_utc,
                renewed_at_utc,
                expires_at_utc,
                revision,
                authority_lifecycle_revision,
                last_renewal_operation_id,
                last_renewal_operation_identity_key)
            SELECT
                lease.profile_id,
                lease.snapshot_id,
                lease.lease_id,
                0,
                lease.server_instance_id,
                lease.restore_round_id,
                lease.acquired_at_utc,
                lease.renewed_at_utc,
                lease.expires_at_utc,
                lease.revision,
                profile.lifecycle_revision + CASE
                    WHEN snapshot.status = 1 THEN 3
                    WHEN snapshot.status = 2 AND EXISTS (
                        SELECT 1 FROM luam_deep_cryo_operation AS operation
                        WHERE operation.snapshot_id = snapshot.lua_m_deep_cryo_snapshots_id
                          AND operation.profile_id = snapshot.profile_id
                          AND operation.kind = 2
                          AND operation.result_status = 2
                          AND operation.result_revision = snapshot.revision
                          AND operation.lease_id = lease.lease_id
                          AND operation.reason IS NULL) THEN 2
                    ELSE 1
                END,
                NULL,
                NULL
            FROM luam_character_presence_lease AS lease
            JOIN profile ON profile.profile_id = lease.profile_id
            JOIN luam_deep_cryo_snapshot AS snapshot
              ON snapshot.lua_m_deep_cryo_snapshots_id = lease.snapshot_id
             AND snapshot.profile_id = lease.profile_id;

            -- The lease epoch is the profile lifecycle epoch after the legacy
            -- Claim/PREPARE/AUTH steps. Advance both sides together; otherwise
            -- every post-migration CAS observes an impossible epoch mismatch.
            UPDATE profile
            SET lifecycle_revision = (
                SELECT authority_lifecycle_revision
                FROM __luam_character_presence_lease_new AS lease
                WHERE lease.profile_id = profile.profile_id)
            WHERE EXISTS (
                SELECT 1
                FROM __luam_character_presence_lease_new AS lease
                WHERE lease.profile_id = profile.profile_id);

            DROP TABLE luam_character_presence_lease;
            ALTER TABLE __luam_character_presence_lease_new
                RENAME TO luam_character_presence_lease;

            CREATE INDEX IX_luam_character_presence_lease_snapshot_id_profile_id
                ON luam_character_presence_lease(snapshot_id, profile_id);
            CREATE INDEX IX_luam_cryo_lease_expires
                ON luam_character_presence_lease(expires_at_utc);
            CREATE INDEX IX_luam_cryo_lease_phase_expires
                ON luam_character_presence_lease(phase, expires_at_utc);
            CREATE UNIQUE INDEX UX_luam_cryo_lease_snapshot
                ON luam_character_presence_lease(snapshot_id)
                WHERE snapshot_id IS NOT NULL;
            CREATE UNIQUE INDEX UX_luam_cryo_lease_token
                ON luam_character_presence_lease(lease_id);

            CREATE TABLE luam_character_presence_operation (
                lua_m_character_presence_operations_id INTEGER NOT NULL
                    CONSTRAINT PK_luam_character_presence_operation PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL,
                operation_identity_key TEXT NOT NULL,
                profile_id INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                result_has_authority INTEGER NOT NULL,
                result_snapshot_id INTEGER NULL,
                result_lease_id TEXT NULL,
                result_phase INTEGER NULL,
                result_server_instance_id TEXT NULL,
                result_round_id INTEGER NULL,
                result_acquired_at_utc TEXT NULL,
                result_renewed_at_utc TEXT NULL,
                result_expires_at_utc TEXT NULL,
                result_lease_revision INTEGER NULL,
                result_authority_lifecycle_revision INTEGER NULL,
                result_lifecycle_revision INTEGER NOT NULL,
                reason TEXT NULL,
                created_at_utc TEXT NOT NULL,
                CONSTRAINT CK_luam_presence_operation_kind CHECK (kind >= 0 AND kind <= 3),
                CONSTRAINT CK_luam_presence_operation_identity CHECK (
                    length(operation_identity_key) = 64),
                CONSTRAINT CK_luam_presence_operation_revision CHECK (
                    result_lifecycle_revision >= 0 AND
                    (result_lease_revision IS NULL OR result_lease_revision >= 0) AND
                    (result_authority_lifecycle_revision IS NULL OR
                     result_authority_lifecycle_revision >= 0)),
                CONSTRAINT CK_luam_presence_operation_authority CHECK (
                    (result_has_authority = 0 AND result_snapshot_id IS NULL AND
                     result_lease_id IS NULL AND result_phase IS NULL AND
                     result_server_instance_id IS NULL AND result_round_id IS NULL AND
                     result_acquired_at_utc IS NULL AND result_renewed_at_utc IS NULL AND
                     result_expires_at_utc IS NULL AND result_lease_revision IS NULL AND
                     result_authority_lifecycle_revision IS NULL) OR
                    (result_has_authority = 1 AND result_lease_id IS NOT NULL AND
                     result_phase IS NOT NULL AND result_phase BETWEEN 0 AND 2 AND
                     result_server_instance_id IS NOT NULL AND result_round_id IS NOT NULL AND
                     result_round_id >= 0 AND result_acquired_at_utc IS NOT NULL AND
                     result_renewed_at_utc IS NOT NULL AND result_expires_at_utc IS NOT NULL AND
                     result_acquired_at_utc <= result_renewed_at_utc AND
                     result_renewed_at_utc < result_expires_at_utc AND
                     result_lease_revision IS NOT NULL AND result_lease_revision >= 0 AND
                     result_authority_lifecycle_revision IS NOT NULL AND
                     result_authority_lifecycle_revision >= 0)),
                CONSTRAINT CK_luam_presence_operation_snapshot_phase CHECK (
                    result_has_authority = 0 OR
                    (result_phase <> 0 OR result_snapshot_id IS NOT NULL)),
                CONSTRAINT CK_luam_presence_operation_kind_result CHECK (
                    (kind = 0 AND result_has_authority = 1 AND result_phase = 1 AND
                     result_snapshot_id IS NULL AND reason IS NULL) OR
                    (kind = 1 AND result_has_authority = 1 AND result_phase = 2 AND
                     result_snapshot_id IS NULL AND reason IS NULL) OR
                    (kind = 2 AND result_has_authority = 0 AND reason IS NOT NULL) OR
                    (kind = 3 AND result_has_authority = 1 AND result_phase = 1 AND
                     result_snapshot_id IS NULL AND reason IS NULL)),
                CONSTRAINT CK_luam_presence_operation_epoch CHECK (
                    result_has_authority = 0 OR
                    result_authority_lifecycle_revision = result_lifecycle_revision),
                CONSTRAINT FK_luam_character_presence_operation_profile_profile_id
                    FOREIGN KEY (profile_id) REFERENCES profile(profile_id) ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX UX_luam_presence_operation_id
                ON luam_character_presence_operation(operation_id);
            CREATE INDEX IX_luam_presence_operation_profile_created
                ON luam_character_presence_operation(profile_id, created_at_utc);

            CREATE TRIGGER TR_luam_presence_operation_no_update
            BEFORE UPDATE ON luam_character_presence_operation
            BEGIN
                SELECT RAISE(ABORT, 'LuaM character-presence operations are append-only');
            END;

            CREATE TRIGGER TR_luam_presence_operation_no_delete
            BEFORE DELETE ON luam_character_presence_operation
            BEGIN
                SELECT RAISE(ABORT, 'LuaM character-presence operations are append-only');
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE __luam_presence_downgrade_guard (
                value INTEGER NOT NULL CHECK (value = 0)
            );
            INSERT INTO __luam_presence_downgrade_guard(value)
            SELECT 1
            WHERE EXISTS (SELECT 1 FROM luam_character_presence_operation)
               OR EXISTS (
                    SELECT 1
                    FROM luam_character_presence_lease
                    WHERE phase <> 0 OR snapshot_id IS NULL OR
                          last_renewal_operation_id IS NOT NULL OR
                          last_renewal_operation_identity_key IS NOT NULL);
            DROP TABLE __luam_presence_downgrade_guard;

            DROP TRIGGER IF EXISTS TR_luam_presence_operation_no_update;
            DROP TRIGGER IF EXISTS TR_luam_presence_operation_no_delete;
            DROP TABLE luam_character_presence_operation;

            CREATE TABLE __luam_character_presence_lease_old (
                profile_id INTEGER NOT NULL,
                snapshot_id INTEGER NOT NULL,
                lease_id TEXT NOT NULL,
                server_instance_id TEXT NOT NULL,
                restore_round_id INTEGER NOT NULL,
                acquired_at_utc TEXT NOT NULL,
                renewed_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                revision INTEGER NOT NULL,
                CONSTRAINT PK_luam_character_presence_lease PRIMARY KEY (profile_id),
                CONSTRAINT CK_luam_cryo_lease_dates CHECK (
                    acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc),
                CONSTRAINT CK_luam_cryo_lease_revision CHECK (revision >= 0),
                CONSTRAINT CK_luam_cryo_lease_round CHECK (restore_round_id >= 0),
                CONSTRAINT FK_luam_character_presence_lease_profile_profile_id1
                    FOREIGN KEY (profile_id) REFERENCES profile(profile_id) ON DELETE RESTRICT,
                CONSTRAINT FK_luam_character_presence_lease_luam_deep_cryo_snapshot_lua_m_deep_cryo_snapshot_id
                    FOREIGN KEY (snapshot_id, profile_id)
                    REFERENCES luam_deep_cryo_snapshot(lua_m_deep_cryo_snapshots_id, profile_id)
                    ON DELETE RESTRICT
            );

            INSERT INTO __luam_character_presence_lease_old (
                profile_id, snapshot_id, lease_id, server_instance_id, restore_round_id,
                acquired_at_utc, renewed_at_utc, expires_at_utc, revision)
            SELECT
                profile_id, snapshot_id, lease_id, server_instance_id, round_id,
                acquired_at_utc, renewed_at_utc, expires_at_utc, revision
            FROM luam_character_presence_lease;

            DROP TABLE luam_character_presence_lease;
            ALTER TABLE __luam_character_presence_lease_old
                RENAME TO luam_character_presence_lease;

            CREATE INDEX IX_luam_character_presence_lease_snapshot_id_profile_id
                ON luam_character_presence_lease(snapshot_id, profile_id);
            CREATE INDEX IX_luam_cryo_lease_expires
                ON luam_character_presence_lease(expires_at_utc);
            CREATE UNIQUE INDEX UX_luam_cryo_lease_snapshot
                ON luam_character_presence_lease(snapshot_id);
            CREATE UNIQUE INDEX UX_luam_cryo_lease_token
                ON luam_character_presence_lease(lease_id);
            """);
    }
}
