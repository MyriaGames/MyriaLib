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
            if (!_retired.TryGetValue(character.Race, out var old)) return false;
            if (RaceProfile.All.ContainsKey(character.Race)) return false;
            if (!RaceProfile.All.TryGetValue(old.NewRace, out var now)) return false;

            int levelUps = Math.Max(0, character.Level - 1);
            for (int i = 0; i < StatIds.Length; i++)
            {
                string id = StatIds[i];
                int delta = now.BaseStatBonus.GetValueOrDefault(id) - old.Base[i]
                          + (now.StatGrowth.GetValueOrDefault(id) - old.Growth[i]) * levelUps;
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
