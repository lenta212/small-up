using Content.Server.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres;

[DbContext(typeof(PostgresServerDbContext))]
[Migration("20260722150000_LuaMCharacterPresenceAuthority")]
public partial class LuaMCharacterPresenceAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $luam$
            BEGIN
                IF EXISTS (
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
                          END) THEN
                    RAISE EXCEPTION 'Cannot infer a safe final restore authority epoch.';
                END IF;
            END;
            $luam$;

            ALTER TABLE luam_character_presence_lease
                DROP CONSTRAINT "CK_luam_cryo_lease_round";
            ALTER TABLE luam_character_presence_lease
                DROP CONSTRAINT "CK_luam_cryo_lease_revision";
            ALTER TABLE luam_character_presence_lease
                RENAME COLUMN restore_round_id TO round_id;
            ALTER TABLE luam_character_presence_lease
                ALTER COLUMN snapshot_id DROP NOT NULL;
            ALTER TABLE luam_character_presence_lease
                ADD COLUMN phase integer NOT NULL DEFAULT 0,
                ADD COLUMN authority_lifecycle_revision bigint NULL,
                ADD COLUMN last_renewal_operation_id uuid NULL,
                ADD COLUMN last_renewal_operation_identity_key character varying(64) NULL;

            UPDATE luam_character_presence_lease AS lease
            SET authority_lifecycle_revision = profile.lifecycle_revision + CASE
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
            FROM profile, luam_deep_cryo_snapshot AS snapshot
            WHERE profile.profile_id = lease.profile_id
              AND snapshot.lua_m_deep_cryo_snapshots_id = lease.snapshot_id
              AND snapshot.profile_id = lease.profile_id;

            -- Keep the profile CAS epoch aligned with the backfilled lease.
            -- A lease epoch ahead of its profile would make all later authority
            -- mutations fail closed forever after this migration.
            UPDATE profile
            SET lifecycle_revision = lease.authority_lifecycle_revision
            FROM luam_character_presence_lease AS lease
            WHERE lease.profile_id = profile.profile_id;

            ALTER TABLE luam_character_presence_lease
                ALTER COLUMN authority_lifecycle_revision SET NOT NULL,
                ALTER COLUMN phase DROP DEFAULT;

            ALTER TABLE luam_character_presence_lease
                ADD CONSTRAINT "CK_luam_cryo_lease_round" CHECK (round_id >= 0),
                ADD CONSTRAINT "CK_luam_cryo_lease_phase" CHECK (phase >= 0 AND phase <= 2),
                ADD CONSTRAINT "CK_luam_cryo_lease_revision" CHECK (
                    revision >= 0 AND authority_lifecycle_revision >= 0),
                ADD CONSTRAINT "CK_luam_cryo_lease_snapshot_phase" CHECK (
                    (phase = 0 AND snapshot_id IS NOT NULL) OR
                    (phase = 1 AND snapshot_id IS NULL) OR phase = 2),
                ADD CONSTRAINT "CK_luam_cryo_lease_last_renewal" CHECK (
                    (last_renewal_operation_id IS NULL AND
                     last_renewal_operation_identity_key IS NULL) OR
                    (last_renewal_operation_id IS NOT NULL AND
                     last_renewal_operation_identity_key IS NOT NULL AND
                     length(last_renewal_operation_identity_key) = 64));

            DROP INDEX "UX_luam_cryo_lease_snapshot";
            CREATE UNIQUE INDEX "UX_luam_cryo_lease_snapshot"
                ON luam_character_presence_lease(snapshot_id)
                WHERE snapshot_id IS NOT NULL;
            CREATE INDEX "IX_luam_cryo_lease_phase_expires"
                ON luam_character_presence_lease(phase, expires_at_utc);

            CREATE TABLE luam_character_presence_operation (
                lua_m_character_presence_operations_id bigint GENERATED BY DEFAULT AS IDENTITY,
                operation_id uuid NOT NULL,
                operation_identity_key character varying(64) NOT NULL,
                profile_id integer NOT NULL,
                kind integer NOT NULL,
                result_has_authority boolean NOT NULL,
                result_snapshot_id bigint NULL,
                result_lease_id uuid NULL,
                result_phase integer NULL,
                result_server_instance_id character varying(128) NULL,
                result_round_id integer NULL,
                result_acquired_at_utc timestamp with time zone NULL,
                result_renewed_at_utc timestamp with time zone NULL,
                result_expires_at_utc timestamp with time zone NULL,
                result_lease_revision bigint NULL,
                result_authority_lifecycle_revision bigint NULL,
                result_lifecycle_revision bigint NOT NULL,
                reason character varying(512) NULL,
                created_at_utc timestamp with time zone NOT NULL,
                CONSTRAINT "PK_luam_character_presence_operation"
                    PRIMARY KEY (lua_m_character_presence_operations_id),
                CONSTRAINT "FK_luam_character_presence_operation_profile_profile_id"
                    FOREIGN KEY (profile_id) REFERENCES profile(profile_id) ON DELETE RESTRICT,
                CONSTRAINT "CK_luam_presence_operation_kind" CHECK (kind >= 0 AND kind <= 3),
                CONSTRAINT "CK_luam_presence_operation_identity" CHECK (
                    length(operation_identity_key) = 64),
                CONSTRAINT "CK_luam_presence_operation_revision" CHECK (
                    result_lifecycle_revision >= 0 AND
                    (result_lease_revision IS NULL OR result_lease_revision >= 0) AND
                    (result_authority_lifecycle_revision IS NULL OR
                     result_authority_lifecycle_revision >= 0)),
                CONSTRAINT "CK_luam_presence_operation_authority" CHECK (
                    (result_has_authority = FALSE AND result_snapshot_id IS NULL AND
                     result_lease_id IS NULL AND result_phase IS NULL AND
                     result_server_instance_id IS NULL AND result_round_id IS NULL AND
                     result_acquired_at_utc IS NULL AND result_renewed_at_utc IS NULL AND
                     result_expires_at_utc IS NULL AND result_lease_revision IS NULL AND
                     result_authority_lifecycle_revision IS NULL) OR
                    (result_has_authority = TRUE AND result_lease_id IS NOT NULL AND
                     result_phase IS NOT NULL AND result_phase BETWEEN 0 AND 2 AND
                     result_server_instance_id IS NOT NULL AND result_round_id IS NOT NULL AND
                     result_round_id >= 0 AND result_acquired_at_utc IS NOT NULL AND
                     result_renewed_at_utc IS NOT NULL AND result_expires_at_utc IS NOT NULL AND
                     result_acquired_at_utc <= result_renewed_at_utc AND
                     result_renewed_at_utc < result_expires_at_utc AND
                     result_lease_revision IS NOT NULL AND result_lease_revision >= 0 AND
                     result_authority_lifecycle_revision IS NOT NULL AND
                     result_authority_lifecycle_revision >= 0)),
                CONSTRAINT "CK_luam_presence_operation_snapshot_phase" CHECK (
                    result_has_authority = FALSE OR
                    (result_phase <> 0 OR result_snapshot_id IS NOT NULL)),
                CONSTRAINT "CK_luam_presence_operation_kind_result" CHECK (
                    (kind = 0 AND result_has_authority = TRUE AND result_phase = 1 AND
                     result_snapshot_id IS NULL AND reason IS NULL) OR
                    (kind = 1 AND result_has_authority = TRUE AND result_phase = 2 AND
                     result_snapshot_id IS NULL AND reason IS NULL) OR
                    (kind = 2 AND result_has_authority = FALSE AND reason IS NOT NULL) OR
                    (kind = 3 AND result_has_authority = TRUE AND result_phase = 1 AND
                     result_snapshot_id IS NULL AND reason IS NULL)),
                CONSTRAINT "CK_luam_presence_operation_epoch" CHECK (
                    result_has_authority = FALSE OR
                    result_authority_lifecycle_revision = result_lifecycle_revision)
            );

            CREATE UNIQUE INDEX "UX_luam_presence_operation_id"
                ON luam_character_presence_operation(operation_id);
            CREATE INDEX "IX_luam_presence_operation_profile_created"
                ON luam_character_presence_operation(profile_id, created_at_utc);

            CREATE FUNCTION luam_reject_character_presence_operation_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $luam$
            BEGIN
                RAISE EXCEPTION 'LuaM character-presence operations are append-only';
            END;
            $luam$;

            CREATE TRIGGER "TR_luam_presence_operation_no_update"
            BEFORE UPDATE ON luam_character_presence_operation
            FOR EACH ROW EXECUTE FUNCTION luam_reject_character_presence_operation_mutation();

            CREATE TRIGGER "TR_luam_presence_operation_no_delete"
            BEFORE DELETE ON luam_character_presence_operation
            FOR EACH ROW EXECUTE FUNCTION luam_reject_character_presence_operation_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $luam$
            BEGIN
                IF EXISTS (SELECT 1 FROM luam_character_presence_operation)
                   OR EXISTS (
                        SELECT 1
                        FROM luam_character_presence_lease
                        WHERE phase <> 0 OR snapshot_id IS NULL OR
                              last_renewal_operation_id IS NOT NULL OR
                              last_renewal_operation_identity_key IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot downgrade generalized character presence while new authority state exists.';
                END IF;
            END;
            $luam$;

            DROP TRIGGER IF EXISTS "TR_luam_presence_operation_no_update"
                ON luam_character_presence_operation;
            DROP TRIGGER IF EXISTS "TR_luam_presence_operation_no_delete"
                ON luam_character_presence_operation;
            DROP FUNCTION IF EXISTS luam_reject_character_presence_operation_mutation();
            DROP TABLE luam_character_presence_operation;

            DROP INDEX "IX_luam_cryo_lease_phase_expires";
            DROP INDEX "UX_luam_cryo_lease_snapshot";

            ALTER TABLE luam_character_presence_lease
                DROP CONSTRAINT "CK_luam_cryo_lease_phase",
                DROP CONSTRAINT "CK_luam_cryo_lease_revision",
                DROP CONSTRAINT "CK_luam_cryo_lease_round",
                DROP CONSTRAINT "CK_luam_cryo_lease_snapshot_phase",
                DROP CONSTRAINT "CK_luam_cryo_lease_last_renewal";
            ALTER TABLE luam_character_presence_lease
                ALTER COLUMN snapshot_id SET NOT NULL;
            ALTER TABLE luam_character_presence_lease
                RENAME COLUMN round_id TO restore_round_id;
            ALTER TABLE luam_character_presence_lease
                DROP COLUMN phase,
                DROP COLUMN authority_lifecycle_revision,
                DROP COLUMN last_renewal_operation_id,
                DROP COLUMN last_renewal_operation_identity_key;
            ALTER TABLE luam_character_presence_lease
                ADD CONSTRAINT "CK_luam_cryo_lease_round" CHECK (restore_round_id >= 0),
                ADD CONSTRAINT "CK_luam_cryo_lease_revision" CHECK (revision >= 0);

            CREATE UNIQUE INDEX "UX_luam_cryo_lease_snapshot"
                ON luam_character_presence_lease(snapshot_id);
            """);
    }
}
