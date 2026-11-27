using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Services.Manager
{
    /// <summary>
    /// v0.3: Zalu, Gavon and Gamato were retired for the story's own peoples. A character of a
    /// retired race is moved to the nearest lore race when it's loaded - the retired race's start
    /// bonuses and per-level growth (baked into the stats at creation and on every level-up) are
    /// swapped for the new race's, so the character ends up exactly as if made with the new race.
    /// Runs on every load path (server, clients, local saves); a no-op for everything else.
    /// </summary>
    public static class RaceMigration
    {
        private sealed record Retired(string NewRace, int[] Base, int Hp, int Mp, int[] Growth, int HpPerLevel, int MpPerLevel);

        // Stat order: STR, DEX, END, INT, SPR - the retired races' values as they shipped.
        private static readonly Dictionary<string, Retired> _retired = new(StringComparer.OrdinalIgnoreCase)
        {
            [CharacterRace.Gavon]  = new(CharacterRace.Faerie, [-3, 6, 1, 8, 6], 10, 30, [0, 2, 1, 3, 2], 3, 6),
            [CharacterRace.Zalu]   = new(CharacterRace.Aoyu,   [6, 1, 7, 2, 3], 25, 10, [2, 1, 2, 1, 2], 5, 2),
            [CharacterRace.Gamato] = new(CharacterRace.Elf,    [3, 4, 4, 4, 3], 15, 25, [1, 2, 1, 2, 2], 5, 3),
        };

        private static readonly string[] StatIds = ["STR", "DEX", "END", "INT", "SPR"];

        /// <summary>The race a retired race moves to; null if <paramref name="race"/> isn't retired.</summary>
        public static string? TargetOf(string race) => _retired.TryGetValue(race, out var r) ? r.NewRace : null;

        /// <summary>
        /// Moves a character of a retired race to its lore race. Only when the old race is no longer
        /// loaded and the new one is (so a data set that still ships the old races is left alone).
        /// Returns whether anything changed.
        /// </summary>
        public static bool Apply(Character character)
        {
            bool moved = MoveRetiredRace(character);
            return Rebalance(character) | moved;
        }

        // ── v0.3 race rebalance ─────────────────────────────────────────────
        // Race bonuses were shrunk so a race flavours a class instead of turning it into another
        // one (a Rotuka Knight grew as much STR per level from its race as from its class). Start
        // bonuses went from 18 points to 10, growth from 8 points per level to 3 (at most 1 per
        // stat). These are the values as they shipped before; HP/MP were not changed.
        private sealed record OldBalance(int[] Base, int[] Growth);
        private static readonly Dictionary<string, OldBalance> _beforeRebalance = new(StringComparer.OrdinalIgnoreCase)
        {
            [CharacterRace.Myralu] = new([4, 1, 6, 4, 3],  [1, 1, 2, 2, 2]),
            [CharacterRace.Rotuka] = new([8, 5, 5, -1, 1], [3, 2, 2, 0, 1]),
            [CharacterRace.Iymva]  = new([3, 7, 2, 3, 3],  [2, 3, 1, 1, 1]),
            [CharacterRace.Elf]    = new([2, 5, 2, 5, 4],  [1, 2, 1, 2, 2]),
            [CharacterRace.Faerie] = new([-3, 7, 0, 6, 8], [0, 2, 1, 2, 3]),
            [CharacterRace.Aoyu]   = new([3, 3, 4, 3, 5],  [1, 1, 2, 1, 3]),
            [CharacterRace.Amato]  = new([5, 3, 4, 3, 3],  [2, 1, 2, 1, 2]),
        };

        /// <summary>
        /// Moves a character's base stats from the old race balance to the current one. Base stats
        /// are 10 + race start bonus + race growth × (level - 1) - points spent by the player are kept
        /// apart (Stats.BonusValues) - so a character whose base stats still match the old balance
        /// exactly is recalculated with the new one. Anything else (already moved, a race unchanged
        /// by the rebalance, stats edited by hand) is left alone, so this is safe on every load.
        /// </summary>
        public static bool Rebalance(Character character)
        {
            if (!_beforeRebalance.TryGetValue(character.Race, out var old)) return false;
            if (!RaceProfile.All.TryGetValue(character.Race, out var now)) return false;

            int levelUps = Math.Max(0, character.Level - 1);
            bool differs = false;
            for (int i = 0; i < StatIds.Length; i++)
            {
                if (character.Stats.GetBase(StatIds[i]) != 10 + old.Base[i] + old.Growth[i] * levelUps) return false;
                if (old.Base[i] != now.BaseStatBonus.GetValueOrDefault(StatIds[i]) ||
                    old.Growth[i] != now.StatGrowth.GetValueOrDefault(StatIds[i])) differs = true;
            }
            if (!differs) return false;   // data set still ships the old balance

            for (int i = 0; i < StatIds.Length; i++)
            {
                string id = StatIds[i];
                character.Stats.SetBase(id, Math.Max(1, 10 + now.BaseStatBonus.GetValueOrDefault(id) + now.StatGrowth.GetValueOrDefault(id) * levelUps));
            }
            return true;
        }

        private static bool MoveRetiredRace(Character character)
        {
            if (!_retired.TryGetValue(character.Race, out var old)) return false;
            if (RaceProfile.All.ContainsKey(character.Race)) return false;
            if (!RaceProfile.All.TryGetValue(old.NewRace, out var now)) return false;

            int levelUps = Math.Max(0, character.Level - 1);
            for (int i = 0; i < StatIds.Length; i++)
            {
                string id = StatIds[i];
                int delta = now.BaseStatBonus.GetValueOrDefault(id) - old.Base[i]
                          + (now.StatGrowth.GetValueOrDefault(id) - old.Growth[i]) * levelUps;
                // (Moves straight to the current balance; Rebalance afterwards then finds nothing to do.)
                character.Stats.SetBase(id, Math.Max(1, character.Stats.GetBase(id) + delta));
            }
            character.Stats.BaseHealth = Math.Max(1, character.Stats.BaseHealth + now.BaseHpBonus - old.Hp + (now.HpPerLevel - old.HpPerLevel) * levelUps);
            character.Stats.BaseMana   = Math.Max(0, character.Stats.BaseMana + now.BaseManaBonus - old.Mp + (now.ManaPerLevel - old.MpPerLevel) * levelUps);
            character.Race = old.NewRace;

            character.CurrentHealth = Math.Min(character.CurrentHealth, character.MaxHealth);
            character.CurrentMana   = Math.Min(character.CurrentMana, character.MaxMana);
            return true;
        }
    }
}
