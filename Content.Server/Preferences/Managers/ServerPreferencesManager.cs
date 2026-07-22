using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Preferences.Managers
{
    /// <summary>
    /// Sends <see cref="MsgPreferencesAndSettings"/> before the client joins the lobby.
    /// Receives <see cref="MsgSelectCharacter"/> and <see cref="MsgUpdateCharacter"/> at any time.
    /// </summary>
    public sealed partial class ServerPreferencesManager : IServerPreferencesManager, IPostInjectInit
    {
        [Dependency] private IServerNetManager _netManager = default!;
        [Dependency] private IConfigurationManager _cfg = default!;
        [Dependency] private IServerDbManager _db = default!;
        [Dependency] private IPlayerManager _playerManager = default!;
        [Dependency] private IDependencyCollection _dependencies = default!;
        [Dependency] private IPrototypeManager _protos = default!;
        [Dependency] private ILogManager _log = default!;
        [Dependency] private UserDbDataManager _userDb = default!;
        [Dependency] private IEntityManager _entityManager = default!;

        // Cache player prefs on the server so we don't need as much async hell related to them.
        private readonly Dictionary<NetUserId, PlayerPrefData> _cachedPlayerPrefs =
            new();
        private readonly object _characterSlotGenerationLock = new();
        private readonly Dictionary<(NetUserId UserId, int Slot), long> _characterSlotGenerations = new();
        private readonly Dictionary<(NetUserId UserId, int Slot), int> _characterProfileIds = new();
        private readonly Dictionary<NetUserId, long> _profileMutationVersions = new();
        private readonly HashSet<NetUserId> _activeProfileMutations = new();
        private readonly LinkedList<ProfileMutationRequest> _profileMutationWaiters = new();
        private readonly Dictionary<NetUserId, long> _refreshRequestEpochs = new();

        public event Action<CharacterSlotIdentityInvalidated>? CharacterSlotIdentityInvalidated;

        private ISawmill _sawmill = default!;

        private sealed class ProfileMutationLease : IDisposable
        {
            private ServerPreferencesManager? _owner;
            private readonly NetUserId[] _userIds;

            public ProfileMutationLease(ServerPreferencesManager owner, NetUserId[] userIds)
            {
                _owner = owner;
                _userIds = userIds;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.EndProfileMutation(_userIds);
            }
        }

        private sealed class ProfileMutationRequest
        {
            public readonly NetUserId[] UserIds;
            public readonly TaskCompletionSource<IDisposable> Completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkedListNode<ProfileMutationRequest>? Node;

            public ProfileMutationRequest(NetUserId[] userIds)
            {
                UserIds = userIds;
            }
        }

        private int MaxCharacterSlots => _cfg.GetCVar(CCVars.GameMaxCharacterSlots);

        public void Init()
        {
            _netManager.RegisterNetMessage<MsgPreferencesAndSettings>();
            _netManager.RegisterNetMessage<MsgSelectCharacter>(HandleSelectCharacterMessage);
            _netManager.RegisterNetMessage<MsgUpdateCharacter>(HandleUpdateCharacterMessage);
            _netManager.RegisterNetMessage<MsgDeleteCharacter>(HandleDeleteCharacterMessage);
            _sawmill = _log.GetSawmill("prefs");
        }

        private void HandleSelectCharacterMessage(MsgSelectCharacter message)
        {
            ObservePreferenceMutation(
                SelectCharacterAsync(message),
                "select character",
                message.MsgChannel.UserId);
        }

        private async Task SelectCharacterAsync(MsgSelectCharacter message)
        {
            var index = message.SelectedCharacterIndex;
            var userId = message.MsgChannel.UserId;
            using var mutation = await AcquireProfileMutationAsync(new[] { userId });

            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                Logger.WarningS("prefs", $"User {userId} tried to modify preferences before they loaded.");
                return;
            }

            if (index < 0 || index >= MaxCharacterSlots)
            {
                return;
            }

            var curPrefs = prefsData.Prefs!;

            if (!curPrefs.Characters.ContainsKey(index))
            {
                // Non-existent slot.
                return;
            }

            var stagedPreferences = new PlayerPreferences(
                curPrefs.Characters,
                index,
                curPrefs.AdminOOCColor);

            if (ShouldStorePrefs(message.MsgChannel.AuthType))
                await _db.SaveSelectedCharacterIndexAsync(message.MsgChannel.UserId, message.SelectedCharacterIndex);

            lock (_characterSlotGenerationLock)
                prefsData.Prefs = stagedPreferences;
        }

        private void HandleUpdateCharacterMessage(MsgUpdateCharacter message)
        {
            var userId = message.MsgChannel.UserId;

            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (message.Profile == null)
                _sawmill.Error($"User {userId} sent a {nameof(MsgUpdateCharacter)} with a null profile in slot {message.Slot}.");
            else
                ObservePreferenceMutation(
                    SetProfile(userId, message.Slot, message.Profile, false),
                    "update character",
                    userId);
        }

        public async Task SetProfile(NetUserId userId, int slot, ICharacterProfile profile,
            bool authoritative = true) // Mono
        {
            using var mutation = await AcquireProfileMutationAsync(new[] { userId });

            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                _sawmill.Error($"Tried to modify user {userId} preferences before they loaded.");
                return;
            }

            if (slot < 0 || slot >= MaxCharacterSlots)
                return;

            var curPrefs = prefsData.Prefs!;
            var session = _playerManager.GetSessionById(userId);

            var isNewSlot = !curPrefs.Characters.ContainsKey(slot);

            profile.EnsureValid(session, _dependencies);
            // Mono
            if (!authoritative && profile is HumanoidCharacterProfile humanoid)
            {
                if (curPrefs.Characters.TryGetValue(slot, out var oldProfile) && oldProfile is HumanoidCharacterProfile oldHumanoid)
                    profile = humanoid.WithBankBalance(oldHumanoid.BankBalance);
                else
                    profile = humanoid.WithBankBalance(HumanoidCharacterProfile.DefaultBalance);
            }

            var profiles = new Dictionary<int, ICharacterProfile>(curPrefs.Characters)
            {
                [slot] = profile
            };
            var stagedPreferences = new PlayerPreferences(profiles, slot, curPrefs.AdminOOCColor);
            int? stableProfileId = null;

            if (ShouldStorePrefs(session.Channel.AuthType))
            {
                await _db.SaveCharacterSlotAsync(
                    userId,
                    profile,
                    slot,
                    preserveBankBalance: !authoritative);

                if (isNewSlot || !TryGetCharacterProfileId(userId, slot, out _))
                {
                    var profileId = await _db.GetCharacterIdAsync(userId, slot);
                    stableProfileId = profileId ??
                                      throw new InvalidOperationException(
                                          $"Stored character profile {userId}:{slot} has no durable identity after save.");
                }
            }

            CharacterSlotIdentityInvalidated? invalidation = null;
            lock (_characterSlotGenerationLock)
            {
                if (isNewSlot)
                    invalidation = InvalidateCharacterSlotIdentityUnsafe(userId, slot);
                if (stableProfileId is { } profileId)
                    SetCharacterProfileIdUnsafe(userId, slot, profileId);
                prefsData.Prefs = stagedPreferences;
            }

            if (invalidation is { } identityInvalidation)
                CharacterSlotIdentityInvalidated?.Invoke(identityInvalidation);
        }

        private async void ObservePreferenceMutation(Task mutation, string operation, NetUserId userId)
        {
            try
            {
                await mutation;
            }
            catch (Exception exception)
            {
                _sawmill.Error($"Could not {operation} for {userId}: {exception}");
                if (!_playerManager.TryGetSessionById(userId, out var session))
                    return;

                try
                {
                    // A database exception may have happened after COMMIT. Reloading
                    // after the failed mutation lease is released makes the durable
                    // snapshot authoritative without guessing the write outcome.
                    await RefreshPreferencesAsync(session, CancellationToken.None);
                }
                catch (Exception refreshException)
                {
                    _sawmill.Error(
                        $"Could not reconcile preferences after failed {operation} for {userId}: {refreshException}");
                }
            }
        }

        public bool TryApplyPersistedBankBalance(NetUserId userId, int slot, int balance)
        {
            lock (_characterSlotGenerationLock)
                return TryApplyPersistedBankBalanceUnsafe(userId, slot, balance);
        }

        private bool TryApplyPersistedBankBalanceUnsafe(NetUserId userId, int slot, int balance)
        {
            if (balance < 0 ||
                slot < 0 ||
                slot >= MaxCharacterSlots ||
                !_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) ||
                !prefsData.PrefsLoaded ||
                prefsData.Prefs is not { } curPrefs ||
                !curPrefs.Characters.TryGetValue(slot, out var profile) ||
                profile is not HumanoidCharacterProfile humanoid)
            {
                return false;
            }

            var profiles = new Dictionary<int, ICharacterProfile>(curPrefs.Characters)
            {
                [slot] = humanoid.WithBankBalance(balance),
            };

            prefsData.Prefs = new PlayerPreferences(
                profiles,
                curPrefs.SelectedCharacterIndex,
                curPrefs.AdminOOCColor);
            AdvanceProfileMutationVersionUnsafe(userId);
            return true;
        }

        public bool TryApplyPersistedBankBalance(
            NetUserId userId,
            int slot,
            long expectedGeneration,
            int balance)
        {
            lock (_characterSlotGenerationLock)
            {
                if (GetCharacterSlotGenerationUnsafe(userId, slot) != expectedGeneration)
                    return false;

                // Keep the identity check and cache projection atomic with respect
                // to lifecycle invalidation. The event itself is raised after the
                // generation lock is released below.
                return TryApplyPersistedBankBalanceUnsafe(userId, slot, balance);
            }
        }

        public bool TryApplyPersistedBankBalance(
            NetUserId userId,
            int slot,
            int expectedProfileId,
            int balance)
        {
            lock (_characterSlotGenerationLock)
            {
                if (!_characterProfileIds.TryGetValue((userId, slot), out var currentProfileId) ||
                    currentProfileId != expectedProfileId)
                {
                    return false;
                }

                return TryApplyPersistedBankBalanceUnsafe(userId, slot, balance);
            }
        }

        public long GetCharacterSlotGeneration(NetUserId userId, int slot)
        {
            lock (_characterSlotGenerationLock)
                return GetCharacterSlotGenerationUnsafe(userId, slot);
        }

        public bool TryGetCharacterProfileId(NetUserId userId, int slot, out int profileId)
        {
            lock (_characterSlotGenerationLock)
                return _characterProfileIds.TryGetValue((userId, slot), out profileId);
        }

        public async Task<IDisposable> AcquireProfileMutationAsync(
            IReadOnlyCollection<NetUserId> userIds,
            CancellationToken cancel = default)
        {
            cancel.ThrowIfCancellationRequested();
            var request = new ProfileMutationRequest(NormalizeProfileMutationUsers(userIds));
            lock (_characterSlotGenerationLock)
            {
                request.Node = _profileMutationWaiters.AddLast(request);
                GrantProfileMutationWaitersUnsafe();
            }

            using var registration = cancel.Register(
                static state =>
                {
                    var (owner, queued, cancellationToken) =
                        ((ServerPreferencesManager, ProfileMutationRequest, CancellationToken)) state!;
                    owner.CancelProfileMutationRequest(queued, cancellationToken);
                },
                (this, request, cancel));
            return await request.Completion.Task;
        }

        public bool TryAcquireProfileMutation(
            IReadOnlyCollection<NetUserId> userIds,
            [NotNullWhen(true)] out IDisposable? lease)
        {
            var distinctUserIds = NormalizeProfileMutationUsers(userIds);
            lock (_characterSlotGenerationLock)
            {
                if (distinctUserIds.Any(userId => _activeProfileMutations.Contains(userId)) ||
                    _profileMutationWaiters.Any(request =>
                        request.UserIds.Any(userId => distinctUserIds.Contains(userId))))
                {
                    lease = null;
                    return false;
                }

                BeginProfileMutationUnsafe(distinctUserIds);
                lease = new ProfileMutationLease(this, distinctUserIds);
                return true;
            }
        }

        private static NetUserId[] NormalizeProfileMutationUsers(IReadOnlyCollection<NetUserId> userIds)
        {
            ArgumentNullException.ThrowIfNull(userIds);
            var distinctUserIds = userIds
                .Distinct()
                .OrderBy(userId => userId.UserId)
                .ToArray();
            if (distinctUserIds.Length == 0)
                throw new ArgumentException("At least one profile-mutation user is required.", nameof(userIds));
            return distinctUserIds;
        }

        private void BeginProfileMutationUnsafe(NetUserId[] userIds)
        {
            DebugTools.Assert(userIds.All(userId => !_activeProfileMutations.Contains(userId)));
            foreach (var userId in userIds)
            {
                _activeProfileMutations.Add(userId);
                AdvanceProfileMutationVersionUnsafe(userId);
            }
        }

        private void GrantProfileMutationWaitersUnsafe()
        {
            var reservedByEarlierWaiters = new HashSet<NetUserId>();
            var node = _profileMutationWaiters.First;
            while (node != null)
            {
                var next = node.Next;
                var request = node.Value;
                if (request.UserIds.Any(userId =>
                        _activeProfileMutations.Contains(userId) ||
                        reservedByEarlierWaiters.Contains(userId)))
                {
                    reservedByEarlierWaiters.UnionWith(request.UserIds);
                    node = next;
                    continue;
                }

                _profileMutationWaiters.Remove(node);
                request.Node = null;
                BeginProfileMutationUnsafe(request.UserIds);
                request.Completion.TrySetResult(new ProfileMutationLease(this, request.UserIds));
                node = next;
            }
        }

        private void CancelProfileMutationRequest(ProfileMutationRequest request, CancellationToken cancel)
        {
            var removed = false;
            lock (_characterSlotGenerationLock)
            {
                if (request.Node?.List == _profileMutationWaiters)
                {
                    _profileMutationWaiters.Remove(request.Node);
                    request.Node = null;
                    removed = true;
                    GrantProfileMutationWaitersUnsafe();
                }
            }

            if (removed)
                request.Completion.TrySetCanceled(cancel);
        }

        private void EndProfileMutation(IReadOnlyCollection<NetUserId> userIds)
        {
            lock (_characterSlotGenerationLock)
            {
                foreach (var userId in userIds)
                {
                    var removed = _activeProfileMutations.Remove(userId);
                    DebugTools.Assert(removed);
                    if (!removed)
                        continue;
                    AdvanceProfileMutationVersionUnsafe(userId);
                }

                GrantProfileMutationWaitersUnsafe();
            }
        }

        private long AdvanceProfileMutationVersionUnsafe(NetUserId userId)
        {
            // Mutation versions are change tokens, not quantities. Deliberate wrap
            // keeps lease acquisition/release exception-safe at the numeric limit.
            var version = unchecked(_profileMutationVersions.GetValueOrDefault(userId) + 1);
            _profileMutationVersions[userId] = version;
            return version;
        }

        private long BeginRefreshRequest(NetUserId userId)
        {
            lock (_characterSlotGenerationLock)
            {
                var epoch = unchecked(_refreshRequestEpochs.GetValueOrDefault(userId) + 1);
                _refreshRequestEpochs[userId] = epoch;
                return epoch;
            }
        }

        private void SetCharacterProfileId(NetUserId userId, int slot, int profileId)
        {
            lock (_characterSlotGenerationLock)
                SetCharacterProfileIdUnsafe(userId, slot, profileId);
        }

        private void SetCharacterProfileIdUnsafe(NetUserId userId, int slot, int profileId)
        {
            _characterProfileIds[(userId, slot)] = profileId;
        }

        private void RemoveCharacterProfileId(NetUserId userId, int slot)
        {
            lock (_characterSlotGenerationLock)
                _characterProfileIds.Remove((userId, slot));
        }

        private void ReplaceCharacterProfileIds(NetUserId userId, IReadOnlyDictionary<int, int> profileIds)
        {
            lock (_characterSlotGenerationLock)
                ReplaceCharacterProfileIdsUnsafe(userId, profileIds);
        }

        private void ReplaceCharacterProfileIdsUnsafe(NetUserId userId, IReadOnlyDictionary<int, int> profileIds)
        {
            foreach (var key in _characterProfileIds.Keys.Where(key => key.UserId == userId).ToArray())
                _characterProfileIds.Remove(key);

            foreach (var (slot, profileId) in profileIds)
                SetCharacterProfileIdUnsafe(userId, slot, profileId);
        }

        private void InvalidateCharacterSlotIdentity(NetUserId userId, int slot)
        {
            CharacterSlotIdentityInvalidated invalidation;
            lock (_characterSlotGenerationLock)
                invalidation = InvalidateCharacterSlotIdentityUnsafe(userId, slot);

            CharacterSlotIdentityInvalidated?.Invoke(invalidation);
        }

        private CharacterSlotIdentityInvalidated InvalidateCharacterSlotIdentityUnsafe(NetUserId userId, int slot)
        {
            var key = (userId, slot);
            var generation = unchecked(GetCharacterSlotGenerationUnsafe(userId, slot) + 1);
            _characterSlotGenerations[key] = generation;
            return new CharacterSlotIdentityInvalidated(userId, slot, generation);
        }

        private long GetCharacterSlotGenerationUnsafe(NetUserId userId, int slot)
        {
            return _characterSlotGenerations.GetValueOrDefault((userId, slot));
        }

        private void InvalidateAllCharacterSlotIdentities(NetUserId userId, PlayerPreferences? replacement = null)
        {
            CharacterSlotIdentityInvalidated[] invalidations;
            lock (_characterSlotGenerationLock)
                invalidations = InvalidateAllCharacterSlotIdentitiesUnsafe(userId, replacement);

            foreach (var invalidation in invalidations)
                CharacterSlotIdentityInvalidated?.Invoke(invalidation);
        }

        private CharacterSlotIdentityInvalidated[] InvalidateAllCharacterSlotIdentitiesUnsafe(
            NetUserId userId,
            PlayerPreferences? replacement = null)
        {
            var slots = new HashSet<int>();
            if (_cachedPlayerPrefs.TryGetValue(userId, out var cached) && cached.Prefs != null)
                slots.UnionWith(cached.Prefs.Characters.Keys);
            if (replacement != null)
                slots.UnionWith(replacement.Characters.Keys);

            return slots
                .Select(slot => InvalidateCharacterSlotIdentityUnsafe(userId, slot))
                .ToArray();
        }

        private void HandleDeleteCharacterMessage(MsgDeleteCharacter message)
        {
            ObservePreferenceMutation(
                DeleteCharacterAsync(message),
                "delete character",
                message.MsgChannel.UserId);
        }

        private async Task DeleteCharacterAsync(MsgDeleteCharacter message)
        {
            var slot = message.Slot;
            var userId = message.MsgChannel.UserId;
            using var mutation = await AcquireProfileMutationAsync(new[] { userId });

            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                Logger.WarningS("prefs", $"User {userId} tried to modify preferences before they loaded.");
                return;
            }

            if (slot < 0 || slot >= MaxCharacterSlots)
            {
                return;
            }

            var curPrefs = prefsData.Prefs!;

            // If they try to delete the slot they have selected then we switch to another one.
            // Of course, that's only if they HAVE another slot.
            int? nextSlot = null;
            if (curPrefs.SelectedCharacterIndex == slot)
            {
                // That ! on the end is because Rider doesn't like .NET 5.
                var (ns, profile) = curPrefs.Characters.FirstOrDefault(p => p.Key != message.Slot)!;
                if (profile == null)
                {
                    // Only slot left, can't delete.
                    return;
                }

                nextSlot = ns;
            }

            var arr = new Dictionary<int, ICharacterProfile>(curPrefs.Characters);
            arr.Remove(slot);
            var stagedPreferences = new PlayerPreferences(
                arr,
                nextSlot ?? curPrefs.SelectedCharacterIndex,
                curPrefs.AdminOOCColor);

            if (ShouldStorePrefs(message.MsgChannel.AuthType))
            {
                if (nextSlot != null)
                {
                    await _db.DeleteSlotAndSetSelectedIndex(userId, slot, nextSlot.Value);
                }
                else
                {
                    await _db.SaveCharacterSlotAsync(userId, null, slot);
                }
            }

            CharacterSlotIdentityInvalidated invalidation;
            lock (_characterSlotGenerationLock)
            {
                _characterProfileIds.Remove((userId, slot));
                invalidation = InvalidateCharacterSlotIdentityUnsafe(userId, slot);
                prefsData.Prefs = stagedPreferences;
            }

            CharacterSlotIdentityInvalidated?.Invoke(invalidation);
        }

        // Should only be called via UserDbDataManager.
        public async Task LoadData(ICommonSession session, CancellationToken cancel)
        {
            using var mutation = await AcquireProfileMutationAsync(new[] { session.UserId }, cancel);
            if (!ShouldStorePrefs(session.Channel.AuthType))
            {
                // Don't store data for guests.
                var prefsData = new PlayerPrefData
                {
                    PrefsLoaded = true,
                    Prefs = new PlayerPreferences(
                        new[] {new KeyValuePair<int, ICharacterProfile>(0, HumanoidCharacterProfile.Random())},
                        0, Color.Transparent)
                };

                lock (_characterSlotGenerationLock)
                {
                    ReplaceCharacterProfileIdsUnsafe(session.UserId, new Dictionary<int, int>());
                    _cachedPlayerPrefs[session.UserId] = prefsData;
                }
            }
            else
            {
                var snapshot = await GetOrCreatePreferencesSnapshotAsync(session.UserId, cancel);
                var prefsData = new PlayerPrefData
                {
                    Prefs = snapshot.Preferences,
                };
                lock (_characterSlotGenerationLock)
                {
                    ReplaceCharacterProfileIdsUnsafe(session.UserId, snapshot.ProfileIdsBySlot);
                    _cachedPlayerPrefs[session.UserId] = prefsData;
                }
            }
        }

        public async Task FinishLoadAsync(ICommonSession session)
        {
            using var mutation = await AcquireProfileMutationAsync(new[] { session.UserId });
            // This is a separate step from the actual database load.
            // Sanitizing preferences requires play time info due to loadouts.
            // And play time info is loaded concurrently from the DB with preferences.
            var prefsData = _cachedPlayerPrefs[session.UserId];
            DebugTools.Assert(prefsData.Prefs != null);
            prefsData.Prefs = SanitizePreferences(session, prefsData.Prefs, _dependencies);

            prefsData.PrefsLoaded = true;

            var msg = new MsgPreferencesAndSettings();
            msg.Preferences = prefsData.Prefs;
            msg.Settings = new GameSettings
            {
                MaxCharacterSlots = MaxCharacterSlots
            };
            _netManager.ServerSendMessage(msg, session.Channel);

            // Frontier: notify other entities that your player data is loaded.
            if (session.AttachedEntity != null)
                _entityManager.EventBus.RaiseLocalEvent(session.AttachedEntity.Value, new PreferencesLoadedEvent(session, prefsData.Prefs));
        }

        public async Task OnClientDisconnectedAsync(ICommonSession session)
        {
            using var mutation = await AcquireProfileMutationAsync(new[] { session.UserId });
            CharacterSlotIdentityInvalidated[] invalidations;
            lock (_characterSlotGenerationLock)
            {
                invalidations = InvalidateAllCharacterSlotIdentitiesUnsafe(session.UserId);
                ReplaceCharacterProfileIdsUnsafe(session.UserId, new Dictionary<int, int>());
                _cachedPlayerPrefs.Remove(session.UserId);
            }

            foreach (var invalidation in invalidations)
                CharacterSlotIdentityInvalidated?.Invoke(invalidation);
        }

        public bool HavePreferencesLoaded(ICommonSession session)
        {
            return _cachedPlayerPrefs.ContainsKey(session.UserId);
        }


        /// <summary>
        /// Tries to get the preferences from the cache
        /// </summary>
        /// <param name="userId">User Id to get preferences for</param>
        /// <param name="playerPreferences">The user preferences if true, otherwise null</param>
        /// <returns>If preferences are not null</returns>
        public bool TryGetCachedPreferences(NetUserId userId,
            [NotNullWhen(true)] out PlayerPreferences? playerPreferences)
        {
            if (_cachedPlayerPrefs.TryGetValue(userId, out var prefs))
            {
                playerPreferences = prefs.Prefs;
                return prefs.Prefs != null;
            }

            playerPreferences = null;
            return false;
        }

        /// <summary>
        /// Retrieves preferences for the given username from storage.
        /// </summary>
        public PlayerPreferences GetPreferences(NetUserId userId)
        {
            var prefs = _cachedPlayerPrefs[userId].Prefs;
            if (prefs == null)
            {
                throw new InvalidOperationException("Preferences for this player have not loaded yet.");
            }

            return prefs;
        }

        /// <summary>
        /// Retrieves preferences for the given username from storage or returns null.
        /// </summary>
        public PlayerPreferences? GetPreferencesOrNull(NetUserId? userId)
        {
            if (userId == null)
                return null;

            if (_cachedPlayerPrefs.TryGetValue(userId.Value, out var pref))
                return pref.Prefs;
            return null;
        }

        private async Task<PlayerPreferencesSnapshot> GetOrCreatePreferencesSnapshotAsync(
            NetUserId userId,
            CancellationToken cancel)
        {
            var snapshot = await _db.GetPlayerPreferencesSnapshotAsync(userId, cancel);
            if (snapshot != null)
                return snapshot;

            await _db.InitPrefsAsync(userId, HumanoidCharacterProfile.Random(), cancel);
            return await _db.GetPlayerPreferencesSnapshotAsync(userId, cancel) ??
                   throw new InvalidOperationException($"Could not load initialized preferences for {userId}.");
        }

        public async Task RefreshPreferencesAsync(ICommonSession session, CancellationToken cancel)
        {
            var refreshEpoch = BeginRefreshRequest(session.UserId);
            using var mutation = await AcquireProfileMutationAsync(new[] { session.UserId }, cancel);
            if (!_cachedPlayerPrefs.TryGetValue(session.UserId, out var prefsData))
                return;

            var snapshot = await _db.GetPlayerPreferencesSnapshotAsync(session.UserId, cancel);
            if (snapshot == null)
                return;

            CharacterSlotIdentityInvalidated[] invalidations;
            lock (_characterSlotGenerationLock)
            {
                if (_refreshRequestEpochs.GetValueOrDefault(session.UserId) != refreshEpoch ||
                    !_cachedPlayerPrefs.TryGetValue(session.UserId, out var currentPrefsData) ||
                    !ReferenceEquals(currentPrefsData, prefsData))
                {
                    return;
                }

                invalidations = InvalidateAllCharacterSlotIdentitiesUnsafe(
                    session.UserId,
                    snapshot.Preferences);
                ReplaceCharacterProfileIdsUnsafe(session.UserId, snapshot.ProfileIdsBySlot);
                prefsData.Prefs = snapshot.Preferences;
                prefsData.PrefsLoaded = true;
            }

            foreach (var invalidation in invalidations)
                CharacterSlotIdentityInvalidated?.Invoke(invalidation);

            var msg = new MsgPreferencesAndSettings
            {
                Preferences = snapshot.Preferences,
                Settings = new GameSettings
                {
                    MaxCharacterSlots = MaxCharacterSlots
                }
            };

            _netManager.ServerSendMessage(msg, session.Channel);
        }


        private PlayerPreferences SanitizePreferences(ICommonSession session, PlayerPreferences prefs, IDependencyCollection collection)
        {
            // Clean up preferences in case of changes to the game,
            // such as removed jobs still being selected.

            return new PlayerPreferences(prefs.Characters.Select(p =>
            {
                return new KeyValuePair<int, ICharacterProfile>(p.Key, p.Value.Validated(session, collection));
            }), prefs.SelectedCharacterIndex, prefs.AdminOOCColor);
        }

        public IEnumerable<KeyValuePair<NetUserId, ICharacterProfile>> GetSelectedProfilesForPlayers(
            List<NetUserId> usernames)
        {
            return usernames
                .Select(p => (_cachedPlayerPrefs[p].Prefs, p))
                .Where(p => p.Prefs != null)
                .Select(p => new KeyValuePair<NetUserId, ICharacterProfile>(p.p, p.Prefs!.SelectedCharacter));
        }

        internal static bool ShouldStorePrefs(LoginType loginType)
        {
            return loginType.HasStaticUserId();
        }

        private sealed class PlayerPrefData
        {
            public bool PrefsLoaded;
            public PlayerPreferences? Prefs;
        }

        void IPostInjectInit.PostInject()
        {
            _userDb.AddOnLoadPlayer(LoadData);
            _userDb.AddOnFinishLoadAsync(FinishLoadAsync);
            _userDb.AddOnPlayerDisconnectAsync(OnClientDisconnectedAsync);
        }
    }

    // Frontier: event for notifying that preferences for a particular player have loaded in.
    public sealed class PreferencesLoadedEvent : EntityEventArgs
    {
        public readonly ICommonSession Session;
        public readonly PlayerPreferences Prefs;

        public PreferencesLoadedEvent(ICommonSession session, PlayerPreferences prefs)
        {
            Session = session;
            Prefs = prefs;
        }
    }
    // End Frontier
}
