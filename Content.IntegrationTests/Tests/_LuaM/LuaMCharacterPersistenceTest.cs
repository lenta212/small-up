#nullable enable

using System.Collections.Generic;
using System.Linq;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Client.Lobby;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMCharacterPersistenceTest
{
    [Test]
    public async Task SqliteMigrationPreservesExistingProfileDataAndSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new SqliteServerDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260702000000_TTSVoice");

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO preference
                (preference_id, user_id, selected_character_slot, admin_ooc_color, mono_coins)
            VALUES
                (4242, '3d30f33e-5f35-4b45-9f4d-664c733bb138', 7, '#FF0000FF', 123456);

            INSERT INTO profile
                (profile_id, slot, char_name, flavor_text, age, bank_balance, sex, gender,
                 species, voice, markings, hair_name, hair_color, facial_hair_name,
                 facial_hair_color, eye_color, skin_color, height, width, spawn_priority,
                 pref_unavailable, company, preference_id)
            VALUES
                (5252, 7, 'Migration Sentinel', 'Preserve every field', 34, 76543, 'Male', 'Male',
                 'Human', 'migration-voice', X'5B5D', 'Afro', '#112233FF', 'Shaved',
                 '#223344FF', '#334455FF', '#445566FF', 1.125, 0.875, 2,
                 1, 'Migration Company', 4242);

            INSERT INTO job (job_id, profile_id, job_name, priority)
            VALUES (6262, 5252, 'Captain', 2);
            """);

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        var profile = await context.Profile
            .Include(p => p.Jobs)
            .SingleAsync(p => p.Id == 5252);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Slot, Is.EqualTo(7));
            Assert.That(profile.IsArchived, Is.False);
            Assert.That(profile.ArchivedAt, Is.Null);
            Assert.That(profile.CharacterName, Is.EqualTo("Migration Sentinel"));
            Assert.That(profile.FlavorText, Is.EqualTo("Preserve every field"));
            Assert.That(profile.Age, Is.EqualTo(34));
            Assert.That(profile.BankBalance, Is.EqualTo(76543));
            Assert.That(profile.Species, Is.EqualTo("Human"));
            Assert.That(profile.Voice, Is.EqualTo("migration-voice"));
            Assert.That(profile.Markings, Is.Not.Null);
            Assert.That(profile.Markings!.RootElement.GetArrayLength(), Is.Zero);
            Assert.That(profile.Height, Is.EqualTo(1.125f));
            Assert.That(profile.Width, Is.EqualTo(0.875f));
            Assert.That(profile.Company, Is.EqualTo("Migration Company"));
            Assert.That(profile.PreferenceId, Is.EqualTo(4242));
            Assert.That(profile.Jobs.Single().Id, Is.EqualTo(6262));
            Assert.That(profile.Jobs.Single().JobName, Is.EqualTo("Captain"));
        });

        var profileForeignKeys = new List<(string Table, string From, string To, string OnDelete)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_list('profile');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                profileForeignKeys.Add((
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(6)));
            }
        }

        var profileIndexes = new Dictionary<string, bool>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA index_list('profile');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                profileIndexes[reader.GetString(1)] = reader.GetBoolean(2);
        }

        Assert.Multiple(() =>
        {
            Assert.That(profileForeignKeys, Does.Contain(("preference", "preference_id", "preference_id", "CASCADE")));
            Assert.That(profileIndexes, Does.ContainKey("IX_profile_preference_id"));
            Assert.That(profileIndexes, Does.ContainKey("IX_profile_slot_preference_id"));
            Assert.That(profileIndexes["IX_profile_slot_preference_id"], Is.True);
        });

        Assert.That(
            async () => await context.Database.ExecuteSqlRawAsync(
                "UPDATE profile SET slot = NULL WHERE profile_id = 5252;"),
            Throws.TypeOf<SqliteException>(),
            "The database must reject a hidden active profile with a null slot.");

        profile.Slot = null;
        profile.IsArchived = true;
        profile.ArchivedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        Assert.That(
            async () => await migrator.MigrateAsync("20260702000000_TTSVoice"),
            Throws.TypeOf<SqliteException>(),
            "Downgrade must fail closed instead of deleting an archived identity.");

        bool preservedIsArchived;
        long? preservedSlot;
        await using (var command = connection.CreateCommand())
        {
            // The requested downgrade can successfully remove later migrations
            // before PreserveCharacterProfiles rejects deletion of this archived
            // identity. Query only that migration's schema instead of the current
            // EF model, which also expects newer deep-cryo columns.
            command.CommandText = "SELECT is_archived, slot FROM profile WHERE profile_id = 5252;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.That(await reader.ReadAsync(), Is.True);
            preservedIsArchived = reader.GetBoolean(0);
            preservedSlot = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            Assert.That(await reader.ReadAsync(), Is.False);
        }

        Assert.Multiple(() =>
        {
            Assert.That(preservedIsArchived, Is.True);
            Assert.That(preservedSlot, Is.Null);
        });
    }

    [Test]
    public async Task SlotReuseCreatesNewIdentityAndExplicitRestoreKeepsArchivedIdentity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        var server = pair.Server;
        var client = pair.Client;
        var clientPrefManager = client.ResolveDependency<IClientPreferencesManager>();
        var serverDb = server.ResolveDependency<IServerDbManager>();
        var clientNetManager = client.ResolveDependency<IClientNetManager>();

        await pair.RunTicksSync(1);
        await PoolManager.WaitUntil(client, () => clientPrefManager.Preferences != null, 600);
        await client.WaitPost(() => clientPrefManager.CreateCharacter(HumanoidCharacterProfile.Random()));
        await pair.RunTicksSync(5);

        var userId = clientNetManager.ServerChannel!.UserId;
        var originalId = await serverDb.GetCharacterIdAsync(userId, 1);
        Assert.That(originalId, Is.Not.Null);
        var archivedId = originalId.GetValueOrDefault();

        await client.WaitPost(() => clientPrefManager.DeleteCharacter(1));
        await pair.RunTicksSync(5);

        Assert.That(await serverDb.GetCharacterIdAsync(userId, 1), Is.Null,
            "Archived profiles must not be exposed as playable slots.");
        var preferencesAfterArchive = await serverDb.GetPlayerPreferencesAsync(userId, default);
        Assert.That(preferencesAfterArchive, Is.Not.Null);
        Assert.That(preferencesAfterArchive!.Characters.ContainsKey(1), Is.False,
            "Archived profiles and their child preferences must be filtered by the database query.");

        await client.WaitPost(() => clientPrefManager.CreateCharacter(HumanoidCharacterProfile.Random()));
        await pair.RunTicksSync(5);

        var replacementId = await serverDb.GetCharacterIdAsync(userId, 1);
        Assert.Multiple(() =>
        {
            Assert.That(replacementId, Is.Not.Null);
            Assert.That(replacementId, Is.Not.EqualTo(originalId),
                "Ordinary character creation must not revive an archived identity.");
        });

        Assert.That(await serverDb.RestoreArchivedCharacterAsync(userId, archivedId, 1), Is.False,
            "Restore must not replace a character occupying the requested slot.");
        Assert.That(await serverDb.RestoreArchivedCharacterAsync(userId, archivedId, int.MaxValue), Is.False,
            "Restore must reject slots outside the configured character-slot range.");
        Assert.That(await serverDb.GetCharacterIdAsync(userId, 1), Is.EqualTo(replacementId));

        await client.WaitPost(() => clientPrefManager.DeleteCharacter(1));
        await pair.RunTicksSync(5);

        Assert.That(await serverDb.RestoreArchivedCharacterAsync(userId, archivedId, 1), Is.True,
            "An archived identity must remain available to an explicit server-side restore.");
        Assert.That(await serverDb.GetCharacterIdAsync(userId, 1), Is.EqualTo(originalId));
        Assert.That(await serverDb.RestoreArchivedCharacterAsync(userId, archivedId, 1), Is.True,
            "Retrying the same restore must be idempotent.");

        await pair.CleanReturnAsync();
    }
}
