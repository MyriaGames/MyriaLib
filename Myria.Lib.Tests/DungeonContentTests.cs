using System.Text.Json;
using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Regestries;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 dungeons: the shipped dungeon data hangs together - every dungeon is a connected chain
/// entered from the overworld, its packs name real monsters, it ends in a boss room, its level
/// band holds, boss drops exist, and every name/description has en and de text.
/// </summary>
[Collection("GameData")]
public class DungeonContentTests
{
    public DungeonContentTests(GameDataFixture _) { }

    private static Room R(int id) => RoomService.AllRooms.First(r => r.Id == id);

    /// <summary>Access level and the level range of each dungeon's normal (non-boss) monsters.</summary>
    public static TheoryData<string, int, int, int> Bands => new()
    {
        { "sunken_grotto",   10, 10, 15 },
        { "rootdeep_barrow", 20, 20, 30 },
        { "ashen_aerie",     30, 30, 40 },
        { "hollow_crypt",    40, 40, 50 },
    };

    [Theory, MemberData(nameof(Bands))]
    public void EachDungeon_IsAConnectedChainFromTheOverworld_EndingInABoss(string id, int access, int minLevel, int maxLevel)
    {
        var dungeon = DungeonRegistry.GetDungeonById(id)!;
        var rooms = dungeon.RoomIds.Select(R).ToList();

        Assert.All(rooms, r =>
        {
            Assert.True(r.IsDungeonRoom, $"room {r.Id}");
            Assert.Equal(access, r.AccessLevel);
            // Exits go both ways.
            foreach (var (dir, target) in r.ExitIds)
                Assert.True(R(target).ExitIds.ContainsValue(r.Id), $"room {r.Id} {dir} -> {target} has no way back");
        });

        // Exactly one way in from outside, at the anchor room.
        var entries = rooms.SelectMany(r => r.ExitIds.Values.Where(t => !R(t).IsDungeonRoom).Select(t => (r.Id, t))).ToList();
        Assert.Equal(dungeon.AnchorRoomId, Assert.Single(entries).Item1);

        // Every room reachable from the anchor.
        var seen = new HashSet<int> { dungeon.AnchorRoomId };
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
            foreach (var next in R(queue.Dequeue()).ExitIds.Values.Where(dungeon.RoomIds.Contains))
                if (seen.Add(next)) queue.Enqueue(next);
        Assert.Equal(dungeon.RoomIds.Count, seen.Count);

        Assert.Contains(rooms, r => r.IsBossRoom);
        foreach (var r in rooms)
        {
            Assert.NotEmpty(r.DungeonPack);
            foreach (var monsterId in r.DungeonPack.Keys)
            {
                var m = MonsterService.GetMonsterById(monsterId);
                Assert.NotNull(m);
                Assert.Contains(monsterId, r.EncounterableMonsters.Keys);
                if (!r.IsBossRoom) Assert.InRange(m!.Level, minLevel, maxLevel);
            }
            if (r.IsBossRoom) Assert.Single(r.DungeonPack);
        }
    }

    [Fact]
    public void BossDrops_AreRealItems_AndAChanceNotACertainty()
    {
        var bosses = DungeonRegistry.GetAll().SelectMany(d => d.RoomIds).Select(R)
            .Where(r => r.IsBossRoom).SelectMany(r => r.DungeonPack.Keys).Select(id => MonsterService.GetMonsterById(id)!);

        foreach (var boss in bosses)
        {
            Assert.NotEmpty(boss.UniqueLootTable);
            foreach (var entry in boss.UniqueLootTable)
            {
                Assert.True(ItemFactory.TryCreateItem(entry.ItemId, out _), $"{boss.Name} drops unknown {entry.ItemId}");
                Assert.InRange(entry.DropChance, 0.01, 0.25);
            }
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void EveryDungeonName_RoomMonsterAndDrop_HasText(string lang)
    {
        var text = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine("Data", "locales", $"{lang}.json")))!;
        var keys = new List<string>();
        foreach (var d in DungeonRegistry.GetAll())
        {
            keys.Add(d.Name);
            foreach (var r in d.RoomIds.Select(R))
            {
                keys.Add(r.Name);
                keys.Add(r.Description);
                foreach (var m in r.DungeonPack.Keys.Select(id => MonsterService.GetMonsterById(id)!))
                {
                    keys.Add(m.Name);
                    keys.Add(m.Description);
                    foreach (var e in m.UniqueLootTable) { keys.Add("item." + e.ItemId); keys.Add("item." + e.ItemId + ".desc"); }
                }
            }
        }
        Assert.All(keys.Distinct(), k => Assert.True(text.ContainsKey(k), $"{lang} is missing {k}"));
    }
}
