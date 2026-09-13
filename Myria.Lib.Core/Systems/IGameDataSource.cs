using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Jobs;
using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Models.BaseModel;
using Myria.Lib.Core.Services;

namespace Myria.Lib.Core.Systems
{
    /// <summary>
    /// Supplies all static game content that <see cref="Myria.Lib.Core.Services.GameService.InitializeGame(IGameDataSource?, IProgress{string}?, bool)"/>
    /// needs, as plain in-memory data — the same shapes each loader already deserializes JSON into.
    /// <para>
    /// Implementations decide where the data actually comes from (a database, a remote call, etc.).
    /// Myria.Lib.Core itself has no persistence-specific knowledge — it only ever sees these plain types.
    /// </para>
    /// </summary>
    public interface IGameDataSource
    {
        List<RaceProfile> GetRaces();
        List<ClassProfile> GetClasses();
        List<MonsterLootTable> GetLootTables();
        List<GameItem> GetItems();
        List<Monster> GetMonsters();
        List<Npc> GetNpcs();
        List<Room> GetRooms();
        Dictionary<string, CraftingRecipe[]> GetRecipes();
        List<Job> GetJobs();
        List<Quest> GetQuests();
        List<SkillData> GetSkills();
        List<EffectDefinition> GetEffects();
        Dictionary<string, string[]> GetStartingItems();
        List<City> GetCities();
        List<Cave> GetCaves();
        List<Dungeon> GetDungeons();
        List<Forest> GetForests();
        (List<RuneWord> Words, List<WordFamily> Families, List<WordPairRelation> Pairs) GetRuneWords();
        List<BaseRuneData> GetBaseRunes();
    }
}
