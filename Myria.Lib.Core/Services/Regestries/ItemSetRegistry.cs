using System.Text.Json;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Items;

namespace Myria.Lib.Core.Services.Regestries
{
    /// <summary>The gear sets (Data/common/item_sets.json) and the bonuses a character's equipped
    /// pieces give.</summary>
    public static class ItemSetRegistry
    {
        private static List<ItemSet> _sets = new();
        private static Dictionary<string, ItemSet> _byItem = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<ItemSet> All => _sets;

        public static void Load(string path = "Data/common/item_sets.json")
        {
            if (!File.Exists(path)) { Load(new List<ItemSet>()); return; }
            var sets = JsonSerializer.Deserialize<List<ItemSet>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            Load(sets);
        }

        public static void Load(List<ItemSet> sets)
        {
            _sets = sets;
            _byItem = new(StringComparer.OrdinalIgnoreCase);
            foreach (var set in sets)
                foreach (var id in set.Items)
                    _byItem[id] = set;
        }

        /// <summary>The set an item belongs to, if any.</summary>
        public static ItemSet? SetOf(string itemId) => _byItem.TryGetValue(itemId, out var s) ? s : null;

        /// <summary>How many different pieces of <paramref name="set"/> the character has equipped.</summary>
        public static int EquippedPieces(Character character, ItemSet set) =>
            character.Equipped.Values.Where(i => i is not null)
                .Select(i => i!.Id).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(id => set.Items.Contains(id, StringComparer.OrdinalIgnoreCase));

        /// <summary>Every bonus the character's equipped set pieces give right now (all reached tiers
        /// of every set they wear).</summary>
        public static IEnumerable<SkillSetBonus> ActiveBonuses(Character character)
        {
            foreach (var set in _sets)
            {
                int pieces = EquippedPieces(character, set);
                if (pieces < 2) continue;
                foreach (var tier in set.Tiers.Where(t => t.Pieces <= pieces))
                    foreach (var bonus in tier.Bonuses)
                        yield return bonus;
            }
        }

        /// <summary>The summed set bonus for one skill (zero when none applies).</summary>
        public static SkillSetBonus BonusFor(Character character, string skillId)
        {
            var sum = new SkillSetBonus { SkillId = skillId };
            foreach (var b in ActiveBonuses(character).Where(b => string.Equals(b.SkillId, skillId, StringComparison.OrdinalIgnoreCase)))
            {
                sum.Power += b.Power;
                sum.ManaCost += b.ManaCost;
                sum.Cooldown += b.Cooldown;
            }
            return sum;
        }
    }
}
