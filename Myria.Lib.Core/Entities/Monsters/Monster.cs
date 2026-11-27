using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Entities.Items;

namespace Myria.Lib.Core.Entities.Monsters
{
    public class Monster : CombatEntity
    {
        public int Id { get; set; }
        public string Description { get; set; }
        public MonsterType Type { get; set; }
        public int Level { get; set; } = 1;
        public long Exp { get; set; }
        public int MinLoot { get; set; } = 25;
        public int MaxLoot { get; set; } = 75;
        public List<Item> LootTable { get; set; } = new(); // optional overrides
        public List<UniqueLootEntry> UniqueLootTable { get; set; } = new();

        /// <summary>
        /// Seconds before this monster reappears after being killed — only meaningful to a
        /// real-time client with a persistent world (see Myria.Mono.World.WorldMonster);
        /// turn-based clients roll a fresh monster per fight and never read this. Defaults to a
        /// fast, roughly-uniform respawn so existing/mod-added monster data that omits it still
        /// behaves reasonably; individual monsters can set a longer value in monsters.json.
        /// </summary>
        public float RespawnSeconds { get; set; } = 5f;

        /// <summary>Max-HP multiplier (dungeon party scaling, v0.3): 1.5 = half again as much HP.</summary>
        public float HealthScale { get; set; } = 1f;

        /// <summary>Multiplier on the damage this monster deals (dungeon party scaling, v0.3) - applied
        /// to the final damage, since attack stats feed a non-linear formula (and STR also adds defense).</summary>
        public float DamageScale { get; set; } = 1f;

        protected override int ExtraBaseHealth =>
            HealthScale == 1f ? 0 : (int)Math.Round((Stats.BaseHealth + TotalEND * 5) * (HealthScale - 1f));
        public bool DropsCorpse =>
            Type != MonsterType.Spirit && Type != MonsterType.Shadow;

        public Monster(int id, string name, Stats stats, string description, long exp)
        {
            Id = id;
            Name = name;
            Stats = stats;
            Description = description;
            CurrentHealth = MaxHealth;
            Exp = exp;
        }

        public void ResetHealth()
        {
            CurrentHealth = MaxHealth;
        }

        /// <summary>
        /// Returns a fresh instance with the same definition but independent health.
        /// Use this when spawning dungeon monsters so the shared template is not mutated.
        /// </summary>
        public Monster Clone() => new Monster(Id, Name, Stats.Clone(), Description, Exp)
        {
            Level            = Level,
            Type             = Type,
            MinLoot          = MinLoot,
            MaxLoot          = MaxLoot,
            LootTable        = new List<Item>(LootTable),
            UniqueLootTable  = new List<UniqueLootEntry>(UniqueLootTable),
            RespawnSeconds   = RespawnSeconds,
            HealthScale      = HealthScale,
            DamageScale      = DamageScale,
        };
    }

}
