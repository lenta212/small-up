using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Preferences;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Preferences.Managers
{
    public readonly record struct CharacterSlotIdentityInvalidated(
        NetUserId UserId,
        int Slot,
        long Generation);

    public interface IServerPreferencesManager
    {
        event Action<CharacterSlotIdentityInvalidated>? CharacterSlotIdentityInvalidated;

        void Init();

        Task LoadData(ICommonSession session, CancellationToken cancel);
        Task FinishLoadAsync(ICommonSession session);
        Task OnClientDisconnectedAsync(ICommonSession session);

        bool TryGetCachedPreferences(NetUserId userId, [NotNullWhen(true)] out PlayerPreferences? playerPreferences);
        PlayerPreferences GetPreferences(NetUserId userId);
        PlayerPreferences? GetPreferencesOrNull(NetUserId? userId);
        IEnumerable<KeyValuePair<NetUserId, ICharacterProfile>> GetSelectedProfilesForPlayers(List<NetUserId> userIds);
        bool HavePreferencesLoaded(ICommonSession session);
        Task RefreshPreferencesAsync(ICommonSession session, CancellationToken cancel);
        Task SetProfile(NetUserId userId, int slot, ICharacterProfile profile,
            bool authoritative = true); // Mono

        long GetCharacterSlotGeneration(NetUserId userId, int slot);

        bool TryGetCharacterProfileId(NetUserId userId, int slot, out int profileId);

        /// <summary>
        /// Waits for and atomically acquires exclusive profile-mutation ownership
        /// for every supplied user. Preference lifecycle operations use this path
        /// so they serialize with bank mutations without blocking the server thread.
        /// </summary>
        Task<IDisposable> AcquireProfileMutationAsync(
            IReadOnlyCollection<NetUserId> userIds,
            CancellationToken cancel = default);

        /// <summary>
        /// Atomically attempts to acquire exclusive profile-mutation ownership for
        /// every supplied user without waiting. Bank operations fail closed when a
        /// lifecycle mutation owns or is already queued for any requested user.
        /// </summary>
        bool TryAcquireProfileMutation(
            IReadOnlyCollection<NetUserId> userIds,
            [NotNullWhen(true)] out IDisposable? lease);

        /// <summary>
        /// Applies a balance that has already been committed by an atomic database
        /// operation to the in-memory preference cache. This method never writes to
        /// the database and never changes the selected character slot.
        /// </summary>
        bool TryApplyPersistedBankBalance(NetUserId userId, int slot, int balance);

        bool TryApplyPersistedBankBalance(
            NetUserId userId,
            int slot,
            long expectedGeneration,
            int balance);

        bool TryApplyPersistedBankBalance(
            NetUserId userId,
            int slot,
            int expectedProfileId,
            int balance);
    }
}
