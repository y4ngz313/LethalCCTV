using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Y4NGZCompany.Facility.Stash;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// #1219 G4. Live sets of the scene objects the CCTV target cache classifies. Lifecycle
    /// patches add each object when Netcode spawns it (or its base Start/Awake runs)
    /// and drop it on despawn or destroy, so a cache rebuild enumerates these lists instead
    /// of paying a scene-wide <c>FindObjectsOfType</c> per collector.
    ///
    /// Objects that existed before the patches were applied, inactive ones included, are
    /// picked up once by <see cref="SeedExisting"/>. Destroyed entries read as Unity-null and
    /// are swept on scene unload and by <see cref="RegistrySet{T}.Prune"/> before each
    /// enumeration, so the lists never hand out dead objects.
    /// </summary>
    internal static class CctvTargetRegistry
    {
        internal sealed class RegistrySet<T> where T : Component
        {
            private readonly List<T> _items;
            private readonly Dictionary<int, int> _indexById;

            internal RegistrySet(int capacity)
            {
                _items = new List<T>(capacity);
                _indexById = new Dictionary<int, int>(capacity);
            }

            internal int Count => _items.Count;
            internal T this[int index] => _items[index];

            internal void Add(T item)
            {
                if (item == null)
                    return;
                int id = item.GetInstanceID();
                if (_indexById.ContainsKey(id))
                    return;
                _indexById[id] = _items.Count;
                _items.Add(item);
            }

            internal void Remove(T item)
            {
                if (ReferenceEquals(item, null))
                    return;
                if (_indexById.TryGetValue(item.GetInstanceID(), out int index))
                    RemoveAt(index);
            }

            /// <summary>Drops destroyed entries. Swap-remove keeps it O(n) without shifting.</summary>
            internal void Prune()
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] == null)
                        RemoveAt(i);
                }
            }

            /// <summary>Copies the live, active entries into <paramref name="destination"/>
            /// (cleared first). Used by collectors that classify across several passes and
            /// need a stable snapshot while the registry keeps changing.</summary>
            internal void CopyActiveTo(List<T> destination)
            {
                destination.Clear();
                Prune();
                for (int i = 0; i < _items.Count; i++)
                {
                    T item = _items[i];
                    if (item.gameObject.activeInHierarchy)
                        destination.Add(item);
                }
            }

            internal void Clear()
            {
                _items.Clear();
                _indexById.Clear();
            }

            private void RemoveAt(int index)
            {
                int last = _items.Count - 1;
                T removed = _items[index];
                if (index != last)
                {
                    T moved = _items[last];
                    _items[index] = moved;
                    // GetInstanceID is valid on a destroyed object; it only reads the cached id.
                    _indexById[moved.GetInstanceID()] = index;
                }
                _items.RemoveAt(last);
                _indexById.Remove(removed.GetInstanceID());
            }
        }

        internal static readonly RegistrySet<GrabbableObject> Items = new RegistrySet<GrabbableObject>(256);
        internal static readonly RegistrySet<EnemyAI> Enemies = new RegistrySet<EnemyAI>(64);
        internal static readonly RegistrySet<EntranceTeleport> Entrances = new RegistrySet<EntranceTeleport>(16);
        internal static readonly RegistrySet<TerminalAccessibleObject> Devices = new RegistrySet<TerminalAccessibleObject>(64);
        internal static readonly RegistrySet<CompanyStashController> Stashes = new RegistrySet<CompanyStashController>(4);

        private static bool _seeded;
        private static bool _sceneHooked;
        private static readonly List<NetworkBehaviour> _scratchBehaviours = new List<NetworkBehaviour>(16);

        /// <summary>
        /// The interior main entrance (<c>entranceId == 0</c>), else the first interior
        /// entrance, else null. Reads <see cref="Entrances"/>, inactive included, so the floor
        /// resolvers never pay a scene-wide <c>FindObjectsByType</c>.
        /// </summary>
        internal static EntranceTeleport FindInteriorMainEntrance()
        {
            Entrances.Prune();
            EntranceTeleport best = null;
            for (int i = 0; i < Entrances.Count; i++)
            {
                EntranceTeleport entrance = Entrances[i];
                if (entrance.isEntranceToBuilding) continue;
                if (entrance.entranceId == 0) return entrance;
                if (best == null) best = entrance;
            }
            return best;
        }

        /// <summary>
        /// Bootstrap entry: hooks scene unloads (a prune, never a scan) and runs
        /// <see cref="SeedExisting"/>. Called once from <c>SurveillanceBootstrap.Initialize</c>
        /// after the lifecycle patches are applied.
        /// </summary>
        internal static void Initialize()
        {
            if (!_sceneHooked)
            {
                _sceneHooked = true;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
            }
            SeedExisting();
        }

        /// <summary>
        /// One-shot pickup of objects that existed before the lifecycle patches were applied,
        /// inactive ones included (their Start may already have run, so no hook will fire
        /// for them again); the cache filters on activeInHierarchy when it reads. Runs once
        /// per process, never from a cache refresh.
        /// </summary>
        internal static void SeedExisting()
        {
            if (_seeded)
                return;
            _seeded = true;

            foreach (GrabbableObject item in Object.FindObjectsByType<GrabbableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Items.Add(item);
            foreach (EnemyAI enemy in Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Enemies.Add(enemy);
            foreach (EntranceTeleport entrance in Object.FindObjectsByType<EntranceTeleport>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Entrances.Add(entrance);
            foreach (TerminalAccessibleObject device in Object.FindObjectsByType<TerminalAccessibleObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Devices.Add(device);
            foreach (CompanyStashController stash in Object.FindObjectsByType<CompanyStashController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Stashes.Add(stash);
        }

        /// <summary>Registers (or, on despawn, drops) every tracked behaviour on a network
        /// object. Reuses one scratch list; main thread only.</summary>
        internal static void RegisterNetworkObject(NetworkObject networkObject, bool spawned)
        {
            if (networkObject == null)
                return;
            _scratchBehaviours.Clear();
            networkObject.GetComponentsInChildren(includeInactive: true, _scratchBehaviours);
            for (int i = 0; i < _scratchBehaviours.Count; i++)
            {
                NetworkBehaviour behaviour = _scratchBehaviours[i];
                if (behaviour.NetworkObject != networkObject)
                    continue;
                if (spawned)
                    Register(behaviour);
                else
                    Unregister(behaviour);
            }
            _scratchBehaviours.Clear();
        }

        internal static void PruneAll()
        {
            Items.Prune();
            Enemies.Prune();
            Entrances.Prune();
            Devices.Prune();
            Stashes.Prune();
        }

        private static void OnSceneUnloaded(Scene scene) => PruneAll();

        internal static void Register(Component component)
        {
            switch (component)
            {
                case GrabbableObject item: Items.Add(item); break;
                case EnemyAI enemy: Enemies.Add(enemy); break;
                case EntranceTeleport entrance: Entrances.Add(entrance); break;
                case TerminalAccessibleObject device: Devices.Add(device); break;
                case CompanyStashController stash: Stashes.Add(stash); break;
            }
        }

        internal static void Unregister(Component component)
        {
            switch (component)
            {
                case GrabbableObject item: Items.Remove(item); break;
                case EnemyAI enemy: Enemies.Remove(enemy); break;
                case EntranceTeleport entrance: Entrances.Remove(entrance); break;
                case TerminalAccessibleObject device: Devices.Remove(device); break;
                case CompanyStashController stash: Stashes.Remove(stash); break;
            }
        }

        /// <summary>Test/teardown helper. Also re-arms <see cref="SeedExisting"/>.</summary>
        internal static void Clear()
        {
            Items.Clear();
            Enemies.Clear();
            Entrances.Clear();
            Devices.Clear();
            Stashes.Clear();
            _seeded = false;
        }
    }

}
