using System.Text.Json;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Systems;

namespace Myria.Lib.Core.Services
{
    public static class MonsterService
    {
        private static readonly string _defaultPath = "Data/common/monsters.json";
        private static List<Monster> _monsterList = new List<Monster>();
        public static List<Monster> LoadMonsters(string path = "")
        {
            string filePath = string.IsNullOrEmpty(path) ? _defaultPath : path;
            if (!File.Exists(filePath))
                return new List<Monster>();

            string json = File.ReadAllText(filePath);
            var monsters = JsonSerializer.Deserialize<List<Monster>>(json) ?? new();
            return LoadMonsters(monsters);
        }

        /// <summary>Loads monster templates from already-parsed data (e.g. read from a database).</summary>
        public static List<Monster> LoadMonsters(List<Monster> monsters)
        {
            _monsterList = monsters;
            return _monsterList;
        }

        public static void SaveMonsters(List<Monster> monsters)
        {
            string json = JsonSerializer.Serialize(monsters, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_defaultPath, json);
        }
        public static Monster? GetMonsterById(int id)
        {
            var monster = _monsterList.FirstOrDefault(m => m.Id == id);
            if (monster == null)
                GameLog.Error($"Monster ID {id} not found in loaded data.");
            return monster;
        }
        /// <summary>
        /// Like <see cref="PickMonsterForFight"/>, but only among monster types
        /// <paramref name="isAvailable"/> allows (v0.3: a room's population of a type can run out) -
        /// null when none are left.
        /// </summary>
        public static Monster? TryPickMonsterForFight(List<Monster> monsters, Dictionary<int, float> chances, Func<int, bool> isAvailable)
        {
            var available = monsters.Where(m => isAvailable(m.Id)).ToList();
            if (available.Count == 0) return null;
            var availableChances = chances.Where(kv => isAvailable(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            return PickMonsterForFight(available, availableChances);
        }

        /// <summary>Group-fight counterpart of <see cref="TryPickMonsterForFight"/> - an empty list
        /// when nothing is available. (Dungeon rooms don't come here: they fight their DungeonRun's pack.)</summary>
        public static List<Monster> TryPickMonstersForGroupFight(
            List<Monster> templates, Dictionary<int, float> chances, Func<int, bool> isAvailable, int max = 5)
        {
            var available = templates.Where(m => isAvailable(m.Id)).ToList();
            if (available.Count == 0) return new List<Monster>();
            var availableChances = chances.Where(kv => isAvailable(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            return PickMonstersForGroupFight(available, availableChances, max);
        }

        public static Monster PickMonsterForFight(List<Monster> monsters, Dictionary<int, float> chances)
        {
            var eligible = monsters.Where(m => chances.ContainsKey(m.Id)).ToList();
            if (eligible.Count == 0)
                return monsters[Random.Shared.Next(monsters.Count)];

            float totalWeight = eligible.Sum(m => chances[m.Id]);
            float roll = Random.Shared.NextSingle() * totalWeight;

            float cumulative = 0;
            foreach (Monster monster in eligible)
            {
                cumulative += chances[monster.Id];
                if (roll < cumulative)
                    return monster;
            }
            return eligible[^1];
        }

        /// <summary>
        /// Picks <paramref name="max"/> monsters for an overworld group fight by weighted-random
        /// selection from templates.
        /// </summary>
        public static List<Monster> PickMonstersForGroupFight(
            List<Monster> templates,
            Dictionary<int, float> chances,
            int max = 5)
        {
            var result = new List<Monster>();
            for (int i = 0; i < max; i++)
                result.Add(PickMonsterForFight(templates, chances).Clone());
            return result;
        }

        /// <summary>
        /// Picks a group of <paramref name="count"/> monsters using the "same + one variant" rule:
        /// one primary monster is rolled, all slots are filled with clones of it, then exactly one
        /// additional slot is re-rolled and may resolve to a different species.
        /// </summary>
        public static List<Monster> PickGroupWithOneVariant(
            List<Monster> templates,
            Dictionary<int, float> chances,
            int count = 3)
        {
            if (templates.Count == 0) return new List<Monster>();

            var primary = PickMonsterForFight(templates, chances);
            var result  = new List<Monster>();

            for (int i = 0; i < count; i++)
                result.Add(primary.Clone());

            if (count >= 2)
                result[^1] = PickMonsterForFight(templates, chances).Clone();

            return result;
        }

    }

}


