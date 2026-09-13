using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Services.Regestries;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Mods;
using Myria.Lib.Core.Services;

namespace Myria.Lib.Core.Services
{
    public static class GameService
    {
        /// <summary>Shared game state (day, time, ticks). Loaded from disk by InitializeGame.</summary>
        public static GameStatus Game { get; private set; } = new();

        /// <summary>Room lookup by ID. Populated by InitializeGame.</summary>
        public static IReadOnlyDictionary<int, Room> Rooms => _rooms;
        private static Dictionary<int, Room> _rooms = new();
        private static List<Monster> _monsters = new();

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires when a character session becomes active (after <see cref="StartSession"/> is called).
        /// Apps subscribe here to perform per-session setup — for example:
        /// <list type="bullet">
        ///   <item>WPF / desktop: call <see cref="DayCycleManager.StartInactivityTimer"/></item>
        ///   <item>Unity: configure <see cref="GameTick"/> values and start a custom timer</item>
        ///   <item>Console: no action needed — ticks advance on character actions only</item>
        /// </list>
        /// On a future server, call <see cref="StartSession"/> once per connecting character.
        /// </summary>
        public static event Action<Character>? SessionStarted;

        // ── Initialization (shared world data — call once at app / server start) ─

        /// <summary>Loads all shared game data. Safe to call before any character logs in.</summary>
        public static bool InitializeGame() => InitializeGame(null);

        /// <summary>
        /// Loads all shared game data and initialises game systems.
        /// <para>
        /// This is the "server start" phase. It does not start per-session features such as
        /// the inactivity timer — call <see cref="StartSession"/> after loading a character.
        /// </para>
        /// <param name="skipGameState">
        /// When <c>true</c>, skips loading persistent game state (day/time) and re-initialising
        /// the day-cycle system. Use this for hot-reloads triggered by mod changes so that
        /// the running game state is not disturbed.
        /// </param>
        /// <param name="source">
        /// When provided, all static game content is loaded from this source instead of the
        /// default JSON files (mod path resolution is skipped entirely in that case). Used by
        /// hosts that keep their own authoritative copy of the content (e.g. MyriaServer's
        /// SQL-backed store) instead of relying on the JSON files directly at runtime.
        /// </param>
        /// </summary>
        public static bool InitializeGame(IProgress<string>? progress, bool skipGameState = false, IGameDataSource? source = null)
        {
            void Report(string step) => progress?.Report(step);

            if (!skipGameState)
            {
                Game = GameStatusService.Load();
                Report("game_status");
            }

            if (source is null)
            {
                RaceProfile.Load(ModLoader.ResolvePath("Data/common/races.json"));
                ClassProfile.Load(ModLoader.ResolvePath("Data/common/classes.json"));
                LootGenerator.Load(ModLoader.ResolvePath("Data/common/loot_tables.json"));
            }
            else
            {
                RaceProfile.Load(source.GetRaces());
                ClassProfile.Load(source.GetClasses());
                LootGenerator.Load(source.GetLootTables());
            }
            Report("profiles");

            if (source is null)
                ItemFactory.LoadItems(ModLoader.ResolvePath("Data/common/items.json"));
            else
                ItemFactory.LoadItems(source.GetItems());
            Report("items");

            _monsters = source is null
                ? MonsterService.LoadMonsters(ModLoader.ResolvePath("Data/common/monsters.json"))
                : MonsterService.LoadMonsters(source.GetMonsters());
            Report("monsters");

            if (source is null)
                NpcService.LoadNpcs(ModLoader.ResolvePath("Data/common/npcs.json"));
            else
                NpcService.LoadNpcs(source.GetNpcs());
            Report("npcs");

            _rooms = source is null
                ? RoomService.LoadRooms(ModLoader.ResolvePath("Data/common/rooms.json"))
                : RoomService.LoadRooms(source.GetRooms());
            if (_rooms.Count == 0)
                throw new Exception("Failed to load rooms — check rooms.json for syntax errors.");
            Report("rooms");

            RoomService.ConnectMonsterRooms(_monsters, RoomService.AllRooms);
            NpcService.ConnectNpcRooms(NpcService.AllNpcs, RoomService.AllRooms);
            Report("connections");

            if (!skipGameState)
            {
                DayCycleManager.Initialize();
                DayCycleManager.DayAdvanced -= GameEvents.FireDayAdvanced;
                DayCycleManager.DayAdvanced += GameEvents.FireDayAdvanced;
                Report("day_cycle");
            }

            if (source is null)
                CraftingService.LoadRecipes(ModLoader.ResolvePath("Data/common/recipes.json"));
            else
                CraftingService.LoadRecipes(source.GetRecipes());
            Report("recipes");

            if (source is null)
                JobManager.LoadJobs(ModLoader.ResolvePath("Data/common/jobs.json"));
            else
                JobManager.LoadJobs(source.GetJobs());
            Report("jobs");

            if (source is null)
                QuestManager.LoadQuests(ModLoader.ResolvePath("Data/common/quests.json"));
            else
                QuestManager.LoadQuests(source.GetQuests());

            // Quest kill/item progress is tracked via GameEvents instead of Inventory/combat
            // reaching into Quest directly (see QuestManager.OnItemReceived/OnMonsterKilled).
            // -= then += so repeated InitializeGame() calls (mod hot-reload) don't double-fire.
            GameEvents.ItemReceived -= QuestManager.OnItemReceived;
            GameEvents.ItemReceived += QuestManager.OnItemReceived;
            GameEvents.MonsterKilled -= QuestManager.OnMonsterKilled;
            GameEvents.MonsterKilled += QuestManager.OnMonsterKilled;
            Report("quests");

            if (source is null)
            {
                SkillFactory.LoadSkills(ModLoader.ResolvePath("Data/common/skills.json"));
                EffectFactory.LoadEffects(ModLoader.ResolvePath("Data/common/effects.json"));
            }
            else
            {
                SkillFactory.LoadSkills(source.GetSkills());
                EffectFactory.LoadEffects(source.GetEffects());
            }
            Report("skills");

            if (source is null)
            {
                DungeonRegistry.Load(ModLoader.ResolvePath("Data/common/dungeons.json"));
                CaveRegistry.Load(ModLoader.ResolvePath("Data/common/caves.json"));
                CityRegistry.Load(ModLoader.ResolvePath("Data/common/cities.json"));
                ForestRegistry.Load(ModLoader.ResolvePath("Data/common/forests.json"));
            }
            else
            {
                DungeonRegistry.Load(source.GetDungeons());
                CaveRegistry.Load(source.GetCaves());
                CityRegistry.Load(source.GetCities());
                ForestRegistry.Load(source.GetForests());
            }
            Report("registries");

            if (source is null)
            {
                RuneWordService.Load(
                    ModLoader.ResolvePath("Data/common/rune_words.json"),
                    ModLoader.ResolvePath("Data/common/rune_families.json"),
                    ModLoader.ResolvePath("Data/common/rune_word_pairs.json"));
                BaseRuneService.Load(ModLoader.ResolvePath("Data/common/base_runes.json"));
                StartingEquipmentService.Load(ModLoader.ResolvePath("Data/common/starting_items.json"));
            }
            else
            {
                var (words, families, pairs) = source.GetRuneWords();
                RuneWordService.Load(words, families, pairs);
                BaseRuneService.Load(source.GetBaseRunes());
                StartingEquipmentService.Load(source.GetStartingItems());
            }
            Report("skill_systems");

            return true;
        }

        // ── Session start (per character / per client connection) ───────────────────

        /// <summary>
        /// Call this once a character character has been loaded and is ready to play.
        /// Fires <see cref="SessionStarted"/> so apps and systems can perform
        /// per-session setup (inactivity timer, UI bindings, etc.).
        /// </summary>
        public static void StartSession(Character character)
        {
            SessionStarted?.Invoke(character);
            GameEvents.FireSessionStarted(character);

            // Route the character's own LeveledUp into the global hub.
            // Named target method prevents duplicate subscriptions on repeated calls.
            character.LeveledUp -= ForwardLevelUp;
            character.LeveledUp += ForwardLevelUp;
        }

        private static void ForwardLevelUp(object? sender, Myria.Lib.Core.Systems.Events.LevelUpEventArgs e)
        {
            if (sender is Character c)
                GameEvents.FireLevelUp(c, e.NewLevel);
        }
    }
}
