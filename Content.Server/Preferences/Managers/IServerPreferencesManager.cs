using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Preferences;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Preferences.Managers
{
    public interface IServerPreferencesManager
    {
        void Init();

        Task LoadData(ICommonSession session, CancellationToken cancel);
        void FinishLoad(ICommonSession session);
        void OnClientDisconnected(ICommonSession session);

        bool TryGetCachedPreferences(NetUserId userId, [NotNullWhen(true)] out PlayerPreferences? playerPreferences);
        PlayerPreferences GetPreferences(NetUserId userId);
        PlayerPreferences? GetPreferencesOrNull(NetUserId? userId);
        IEnumerable<KeyValuePair<NetUserId, ICharacterProfile>> GetSelectedProfilesForPlayers(List<NetUserId> userIds);
        bool HavePreferencesLoaded(ICommonSession session);
        Task RefreshPreferencesAsync(ICommonSession session, CancellationToken cancel);
        Task SetProfile(NetUserId userId, int slot, ICharacterProfile profile,
            bool authoritative = true); // Mono

        /// <summary>
        /// Applies a balance that has already been committed by an atomic database
        /// operation to the in-memory preference cache. This method never writes to
        /// the database and never changes the selected character slot.
        /// </summary>
        bool TryApplyPersistedBankBalance(NetUserId userId, int slot, int balance);
    }
}
