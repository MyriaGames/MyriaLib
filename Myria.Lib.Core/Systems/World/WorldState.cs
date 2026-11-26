using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Services;

namespace Myria.Lib.Core.Systems.World
{
    // ── v0.3 quest rework, part 1: the living world state ─────────────────────────────────────
    // NPC stock and room monster populations, the two numbers dynamic quests are generated from
    // (see v0.3.md, "Quest system rework"). Pure and server-agnostic here; the Realm server owns the
    // single live instance, ticks it from a background service and persists it.

    /// <summary>How an NPC's stock of an item goes down.</summary>
    public enum StockDrain
    {
        /// <summary>Real activity drains it: players buying the item, or buying something the NPC
        /// makes from it.</summary>
        Sales,
        /// <summary>No modelled activity uses it up, so a slow generic drain stands in (placeholder
        /// until Myria.Mono can simulate the NPC's actual use of it).</summary>
        Passive,
    }

    /// <summary>One NPC *instance* (NPC ids are templates reused across rooms, so the room is part
    /// of the key) and one item it keeps stock of.</summary>
    public readonly record struct StockKey(int RoomId, string NpcId, string ItemId);

    public readonly record struct PopulationKey(int RoomId, int MonsterId);

    public sealed record NpcNeed(StockKey Key, StockDrain Drain);

    /// <summary>All world-state numbers in one place. Every value is a placeholder pending balance.</summary>
    public static class WorldRules
    {
        public static TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Stock an NPC wants to hold; fresh NPCs start here.</summary>
        public const int StockTarget = 20;
        /// <summary>Below this the NPC starts asking for more (fetch quests).</summary>
        public const int StockThreshold = 10;
        /// <summary>Deliveries beyond this aren't taken into stock.</summary>
        public const int StockMax = 40;
        public const int PassiveDrainPerTick = 1;

        /// <summary>A fresh room starts with this many of each monster type.</summary>
        public const int PopulationStart = 25;
        public const int PopulationCap = 40;
        /// <summary>Above this the room is overrun (kill quests).</summary>
        public const int PopulationThreshold = 30;
        /// <summary>Kill quests aim to bring an overrun room back down to this.</summary>
        public const int PopulationRestLevel = 20;
        public const int PopulationGrowthPerTick = 3;
    }

    public static class WorldDerivation
    {
        /// <summary>
        /// What every NPC instance keeps stock of: the raw ingredients of its recipes and the items
        /// it sells (user decision). Sold items drain through sales; an ingredient drains through
        /// sales if the NPC sells something made from it, otherwise passively.
        /// </summary>
        public static List<NpcNeed> DeriveNeeds(IEnumerable<Room> rooms)
        {
            var needs = new Dictionary<StockKey, StockDrain>();
            foreach (var room in rooms)
            foreach (var npc in room.NpcRefs)
            {
                var sold = new HashSet<string>(npc.ItemNames, StringComparer.OrdinalIgnoreCase);
                foreach (var itemId in sold)
                    needs[new StockKey(room.Id, npc.Id, itemId)] = StockDrain.Sales;

                foreach (var recipe in CraftingService.GetRecipes(npc.Id))
                foreach (var ing in recipe.Ingredients)
                {
                    var key = new StockKey(room.Id, npc.Id, ing.ItemId);
                    var drain = sold.Contains(recipe.OutputId) ? StockDrain.Sales : StockDrain.Passive;
                    // Sales wins if any route drains it for real.
                    if (!needs.TryGetValue(key, out var existing) || existing == StockDrain.Passive)
                        needs[key] = drain;
                }
            }
            return needs.Select(kv => new NpcNeed(kv.Key, kv.Value)).ToList();
        }

        /// <summary>Population is tracked for overworld rooms with random encounters only - dungeons
        /// keep their own spawn-and-clear cycle.</summary>
        public static List<PopulationKey> DerivePopulations(IEnumerable<Room> rooms) =>
            rooms.Where(r => !r.IsDungeonRoom && r.EncounterableMonsters.Count > 0)
                 .SelectMany(r => r.EncounterableMonsters.Keys.Select(m => new PopulationKey(r.Id, m)))
                 .ToList();
    }

    /// <summary>
    /// The live NPC stock and room populations. Thread-safe (hub calls from many connections plus
    /// the server's tick all touch it). Keys not derived from the current world data are dropped,
    /// so content changes never leave stale entries behind.
    /// </summary>
    public sealed class WorldState
    {
        private readonly object _sync = new();
        private readonly Dictionary<StockKey, int> _stock = new();
        private readonly Dictionary<StockKey, StockDrain> _drain = new();
        private readonly Dictionary<PopulationKey, int> _population = new();

        public WorldState(IEnumerable<NpcNeed> needs, IEnumerable<PopulationKey> populations,
            IReadOnlyDictionary<StockKey, int>? savedStock = null,
            IReadOnlyDictionary<PopulationKey, int>? savedPopulation = null)
        {
            foreach (var need in needs)
            {
                _drain[need.Key] = need.Drain;
                _stock[need.Key] = savedStock != null && savedStock.TryGetValue(need.Key, out var s)
                    ? Math.Clamp(s, 0, WorldRules.StockMax) : WorldRules.StockTarget;
            }
            foreach (var key in populations)
                _population[key] = savedPopulation != null && savedPopulation.TryGetValue(key, out var p)
                    ? Math.Clamp(p, 0, WorldRules.PopulationCap) : WorldRules.PopulationStart;
        }

        // ── Queries ───────────────────────────────────────────────────────────

        public bool HasNeed(StockKey key) { lock (_sync) return _stock.ContainsKey(key); }
        public int GetStock(StockKey key) { lock (_sync) return _stock.TryGetValue(key, out var v) ? v : 0; }
        public int GetPopulation(PopulationKey key) { lock (_sync) return _population.TryGetValue(key, out var v) ? v : 0; }
        public bool IsTracked(PopulationKey key) { lock (_sync) return _population.ContainsKey(key); }

        /// <summary>Whether a monster type can currently be met in a room: untracked (dungeon,
        /// unknown) means yes; a tracked population at zero means no until it regrows.</summary>
        public bool IsMonsterAvailable(int roomId, int monsterId)
        {
            lock (_sync)
                return !_population.TryGetValue(new PopulationKey(roomId, monsterId), out var v) || v > 0;
        }

        /// <summary>How many items it takes to bring this NPC back to its target stock - 0 unless
        /// it has actually fallen below the threshold (that's when it starts asking).</summary>
        public int StockGap(StockKey key)
        {
            lock (_sync)
                return _stock.TryGetValue(key, out var v) && v < WorldRules.StockThreshold ? WorldRules.StockTarget - v : 0;
        }

        /// <summary>How many kills it takes to bring an overrun room back to its rest level - 0
        /// unless it's above the threshold.</summary>
        public int PopulationGap(PopulationKey key)
        {
            lock (_sync)
                return _population.TryGetValue(key, out var v) && v > WorldRules.PopulationThreshold ? v - WorldRules.PopulationRestLevel : 0;
        }

        public IReadOnlyList<StockKey> StockKeys { get { lock (_sync) return _stock.Keys.ToList(); } }
        public IReadOnlyList<PopulationKey> PopulationKeys { get { lock (_sync) return _population.Keys.ToList(); } }

        // ── Changes ───────────────────────────────────────────────────────────

        /// <summary>One world tick: populations grow (up to the cap), passively drained stock shrinks.</summary>
        public void Tick()
        {
            lock (_sync)
            {
                foreach (var key in _population.Keys.ToList())
                    _population[key] = Math.Min(WorldRules.PopulationCap, _population[key] + WorldRules.PopulationGrowthPerTick);
                foreach (var key in _stock.Keys.ToList())
                    if (_drain[key] == StockDrain.Passive)
                        _stock[key] = Math.Max(0, _stock[key] - WorldRules.PassiveDrainPerTick);
            }
        }

        /// <summary>Any kill of a tracked monster in a tracked room lowers its population - whether
        /// or not the killer holds a quest for it.</summary>
        public void RecordKill(int roomId, int monsterId)
        {
            lock (_sync)
            {
                var key = new PopulationKey(roomId, monsterId);
                if (_population.TryGetValue(key, out var v)) _population[key] = Math.Max(0, v - 1);
            }
        }

        /// <summary>A player bought <paramref name="quantity"/> of <paramref name="itemId"/> from this
        /// NPC: the item itself leaves its stock, and so do the ingredients it's made from here.</summary>
        public void RecordPurchase(int roomId, string npcId, string itemId, int quantity,
            IEnumerable<(string ItemId, int Amount)> ingredientsPerUnit)
        {
            if (quantity <= 0) return;
            lock (_sync)
            {
                Drain(new StockKey(roomId, npcId, itemId), quantity);
                foreach (var (ing, amount) in ingredientsPerUnit)
                    Drain(new StockKey(roomId, npcId, ing), (long)amount * quantity);
            }
        }

        private void Drain(StockKey key, long amount)
        {
            if (_stock.TryGetValue(key, out var v)) _stock[key] = (int)Math.Max(0, v - amount);
        }

        /// <summary>Delivers items into an NPC's stock (shop sale or quest turn-in - the same
        /// operation either way). Returns how many were taken (up to the stock max; 0 if the NPC
        /// doesn't keep this item).</summary>
        public int Deposit(StockKey key, int quantity)
        {
            if (quantity <= 0) return 0;
            lock (_sync)
            {
                if (!_stock.TryGetValue(key, out var v)) return 0;
                int taken = Math.Min(quantity, WorldRules.StockMax - v);
                if (taken <= 0) return 0;
                _stock[key] = v + taken;
                return taken;
            }
        }

        /// <summary>Of the given NPCs in a room, the one that keeps <paramref name="itemId"/> and is
        /// lowest on it - where a plain shop sale of that item goes.</summary>
        public StockKey? NeediestNpc(int roomId, IEnumerable<string> npcIds, string itemId)
        {
            lock (_sync)
                return npcIds.Select(n => new StockKey(roomId, n, itemId))
                             .Where(_stock.ContainsKey)
                             .OrderBy(k => _stock[k])
                             .Select(k => (StockKey?)k)
                             .FirstOrDefault();
        }

        // ── Persistence ───────────────────────────────────────────────────────

        public Dictionary<StockKey, int> SnapshotStock() { lock (_sync) return new(_stock); }
        public Dictionary<PopulationKey, int> SnapshotPopulation() { lock (_sync) return new(_population); }
    }
}
