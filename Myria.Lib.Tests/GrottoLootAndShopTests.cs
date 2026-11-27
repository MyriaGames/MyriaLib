using System.Text.Json;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 gear economy: NPCs sell one (middle) tier of equipment that still covers every class and
/// slot; the Sunken Grotto drops two sets - the stage boss's (Silt Matron, about a level-20 smith's
/// work, higher drop chance) and Old Breswelc's (about a level-50 smith's, rarer and better).
/// </summary>
[Collection("GameData")]
public class GrottoLootAndShopTests
{
    public GrottoLootAndShopTests(GameDataFixture _) { }

    private const int SiltMatron = 56, OldBreswelc = 43;

    private static readonly string[] SelectableClasses =
        { "Knight", "Barbarian", "Fighter", "SoulsKnight", "Rogue", "Archer", "Hunter", "Cleric", "ArcanMage", "ElementalMage", "Druid" };

    private static readonly string[] Slots =
        { Myria.Lib.Core.Systems.Enums.EquipmentType.Weapon, Myria.Lib.Core.Systems.Enums.EquipmentType.Armor, Myria.Lib.Core.Systems.Enums.EquipmentType.Accessory };

    private static List<EquipmentItem> Loot(int monsterId) =>
        MonsterService.GetMonsterById(monsterId)!.UniqueLootTable
            .Select(e => ItemFactory.CreateItem(e.ItemId) as EquipmentItem).OfType<EquipmentItem>().ToList();

    private static int Power(EquipmentItem e) =>
        e.BaseStats.ATK + e.BaseStats.MATK + e.BaseStats.DEF + e.BaseStats.MDEF + e.BaseStats.HP / 2;

    [Theory]
    [InlineData(SiltMatron)]
    [InlineData(OldBreswelc)]
    public void EachSet_HasAWeaponArmourAndAccessory_ForEveryClass(int monsterId)
    {
        var set = Loot(monsterId);
        Assert.Equal(8, set.Count);
        foreach (var cls in SelectableClasses)
            foreach (var slot in Slots)
                Assert.True(set.Any(i => i.SlotType == slot && i.AllowedClasses.Contains(cls)), $"{cls} has nothing in slot {slot} from monster {monsterId}");
    }

    [Fact]
    public void BreswelcSet_IsStrongerAndRarer_ThanTheStageBossSet()
    {
        var stage = Loot(SiltMatron);
        var boss = Loot(OldBreswelc);
        foreach (var b in boss)
        {
            var match = stage.Single(s => s.SlotType == b.SlotType && s.AllowedClasses.SequenceEqual(b.AllowedClasses));
            Assert.True(Power(b) > Power(match) * 1.3, $"{b.Id} vs {match.Id}");
        }
        float stageChance = MonsterService.GetMonsterById(SiltMatron)!.UniqueLootTable.Min(e => e.DropChance);
        float bossChance = MonsterService.GetMonsterById(OldBreswelc)!.UniqueLootTable.Max(e => e.DropChance);
        Assert.True(stageChance > bossChance);
    }

    [Fact]
    public void TheStageBoss_HasItsOwnRoom_InTheMiddleOfTheGrotto()
    {
        var room = RoomService.GetRoomById(77);
        Assert.True(room.IsBossRoom);
        Assert.Equal(1, room.DungeonPack[SiltMatron]);
    }

    /// <summary>Everything NPCs sell (NPC stock and the equipment shop) is middle tier, and every
    /// selectable class can buy something for each slot.</summary>
    [Fact]
    public void NpcShops_SellOnlyTheMiddleTier_AndCoverEveryClassAndSlot()
    {
        using var shopsDoc = JsonDocument.Parse(File.ReadAllText("Data/common/shops.json"));
        var sold = NpcService.AllNpcs.SelectMany(n => n.ItemNames)
            .Concat(shopsDoc.RootElement.EnumerateArray().SelectMany(s => s.GetProperty("itemIds").EnumerateArray().Select(x => x.GetString()!)))
            .Select(id => ItemFactory.CreateItem(id) as EquipmentItem).OfType<EquipmentItem>()
            .Where(e => e.ToolType is null).DistinctBy(e => e.Id).ToList();

        Assert.All(sold, e => Assert.InRange(e.BuyPrice, 150, 600));
        foreach (var cls in SelectableClasses)
            foreach (var slot in Slots)
                Assert.True(sold.Any(i => i.SlotType == slot && i.AllowedClasses.Contains(cls)), $"NPCs sell {cls} nothing for slot {slot}");
    }
}
