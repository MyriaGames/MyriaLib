using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems.World;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 quest rework, part 1: NPC stock and room populations (WorldState), derived from the real
/// world data. Room 1 (Royas) has smith_default, whose recipes turn iron_ore into iron_ingot and
/// iron_ingot into gear it sells (e.g. iron_sword); room 2 is an overworld room with monsters 33-36.
/// </summary>
[Collection("GameData")]
public class WorldStateTests
{
    public WorldStateTests(GameDataFixture _) { }

    private static WorldState FreshWorld() =>
        new(WorldDerivation.DeriveNeeds(RoomService.AllRooms), WorldDerivation.DerivePopulations(RoomService.AllRooms));

    private static readonly StockKey SmithOre    = new(1, "smith_default", "iron_ore");
    private static readonly StockKey SmithIngot  = new(1, "smith_default", "iron_ingot");
    private static readonly StockKey SmithSword  = new(1, "smith_default", "iron_sword");
    private static readonly PopulationKey Room2Wolf = new(2, 33);

    // ── Derivation ────────────────────────────────────────────────────────

    [Fact]
    public void Needs_ComeFromRecipeIngredientsAndSoldItems_PerNpcInstance()
    {
        var needs = WorldDerivation.DeriveNeeds(RoomService.AllRooms).ToDictionary(n => n.Key, n => n.Drain);

        Assert.Equal(StockDrain.Sales, needs[SmithSword]);   // sold
        Assert.Equal(StockDrain.Sales, needs[SmithIngot]);   // ingredient of gear it sells
        Assert.Equal(StockDrain.Passive, needs[SmithOre]);   // ingredient of an ingot it doesn't sell
        // Same NPC template in another city is a separate instance with its own stock.
        Assert.Contains(needs.Keys, k => k.NpcId == "smith_default" && k.RoomId != 1);
    }

    [Fact]
    public void Populations_CoverOverworldEncounterRoomsOnly()
    {
        var keys = WorldDerivation.DerivePopulations(RoomService.AllRooms);

        Assert.Contains(Room2Wolf, keys);
        var dungeonRoomIds = RoomService.AllRooms.Where(r => r.IsDungeonRoom).Select(r => r.Id).ToHashSet();
        Assert.DoesNotContain(keys, k => dungeonRoomIds.Contains(k.RoomId));
    }

    // ── Ticks, kills, availability ────────────────────────────────────────

    [Fact]
    public void Tick_GrowsPopulationsToTheCap_AndDrainsOnlyPassiveStock()
    {
        var world = FreshWorld();

        world.Tick();

        Assert.Equal(WorldRules.PopulationStart + WorldRules.PopulationGrowthPerTick, world.GetPopulation(Room2Wolf));
        Assert.Equal(WorldRules.StockTarget - WorldRules.PassiveDrainPerTick, world.GetStock(SmithOre));
        Assert.Equal(WorldRules.StockTarget, world.GetStock(SmithSword));

        for (int i = 0; i < 100; i++) world.Tick();
        Assert.Equal(WorldRules.PopulationCap, world.GetPopulation(Room2Wolf));
        Assert.Equal(0, world.GetStock(SmithOre));
    }

    [Fact]
    public void Kills_LowerPopulation_AndAnEmptyTypeStopsBeingAvailable()
    {
        var world = FreshWorld();

        for (int i = 0; i < WorldRules.PopulationStart; i++) world.RecordKill(2, 33);

        Assert.Equal(0, world.GetPopulation(Room2Wolf));
        Assert.False(world.IsMonsterAvailable(2, 33));
        Assert.True(world.IsMonsterAvailable(2, 34));
        world.RecordKill(2, 33);
        Assert.Equal(0, world.GetPopulation(Room2Wolf)); // never negative
        Assert.True(world.IsMonsterAvailable(9999, 33));  // untracked rooms are unaffected
    }

    [Fact]
    public void PickingAMonster_SkipsTypesWithNoPopulationLeft()
    {
        var world = FreshWorld();
        var room = RoomService.GetRoomById(2)!;
        foreach (var id in new[] { 33, 34, 35 })
            for (int i = 0; i < WorldRules.PopulationStart; i++) world.RecordKill(2, id);

        for (int i = 0; i < 50; i++)
        {
            var picked = MonsterService.TryPickMonsterForFight(room.Monsters, room.EncounterableMonsters, m => world.IsMonsterAvailable(2, m));
            Assert.Equal(36, picked!.Id);
        }

        for (int i = 0; i < WorldRules.PopulationStart; i++) world.RecordKill(2, 36);
        Assert.Null(MonsterService.TryPickMonsterForFight(room.Monsters, room.EncounterableMonsters, m => world.IsMonsterAvailable(2, m)));
        Assert.Empty(MonsterService.TryPickMonstersForGroupFight(room.Monsters, room.EncounterableMonsters,
            m => world.IsMonsterAvailable(2, m)));
    }

    // ── Stock: purchases, deposits, gaps ──────────────────────────────────

    [Fact]
    public void Purchase_DrainsTheItemAndItsIngredients_ButNeverBelowZero()
    {
        var world = FreshWorld();

        world.RecordPurchase(1, "smith_default", "iron_sword", 2, new[] { ("iron_ingot", 3) });

        Assert.Equal(WorldRules.StockTarget - 2, world.GetStock(SmithSword));
        Assert.Equal(WorldRules.StockTarget - 6, world.GetStock(SmithIngot));

        world.RecordPurchase(1, "smith_default", "iron_sword", 1000, Array.Empty<(string, int)>());
        Assert.Equal(0, world.GetStock(SmithSword));
    }

    [Fact]
    public void Deposit_FillsUpToTheMax_AndIgnoresItemsTheNpcDoesntKeep()
    {
        var world = FreshWorld();

        Assert.Equal(WorldRules.StockMax - WorldRules.StockTarget, world.Deposit(SmithOre, 1000));
        Assert.Equal(WorldRules.StockMax, world.GetStock(SmithOre));
        Assert.Equal(0, world.Deposit(new StockKey(1, "smith_default", "not_an_item"), 5));
    }

    [Fact]
    public void Gaps_OnlyAppearPastTheirThresholds()
    {
        var world = FreshWorld();
        Assert.Equal(0, world.StockGap(SmithOre));
        Assert.Equal(0, world.PopulationGap(Room2Wolf));

        while (world.GetStock(SmithOre) >= WorldRules.StockThreshold || world.GetPopulation(Room2Wolf) <= WorldRules.PopulationThreshold)
            world.Tick();

        Assert.Equal(WorldRules.StockTarget - world.GetStock(SmithOre), world.StockGap(SmithOre));
        Assert.Equal(world.GetPopulation(Room2Wolf) - WorldRules.PopulationRestLevel, world.PopulationGap(Room2Wolf));
    }

    [Fact]
    public void NeediestNpc_PicksTheLowestStockAmongThoseKeepingTheItem()
    {
        var world = FreshWorld();
        var room1Npcs = RoomService.GetRoomById(1)!.Npcs;

        world.RecordPurchase(1, "smith_default", "iron_sword", 5, Array.Empty<(string, int)>());

        Assert.Equal(SmithSword, world.NeediestNpc(1, room1Npcs, "iron_sword"));
        Assert.Null(world.NeediestNpc(1, room1Npcs, "not_an_item"));
    }

    [Fact]
    public void SavedValues_AreRestored_AndStaleKeysDropped()
    {
        var needs = WorldDerivation.DeriveNeeds(RoomService.AllRooms);
        var pops  = WorldDerivation.DerivePopulations(RoomService.AllRooms);
        var stale = new StockKey(1, "smith_default", "removed_item");

        var world = new WorldState(needs, pops,
            new Dictionary<StockKey, int> { [SmithOre] = 3, [stale] = 7 },
            new Dictionary<PopulationKey, int> { [Room2Wolf] = 11 });

        Assert.Equal(3, world.GetStock(SmithOre));
        Assert.Equal(11, world.GetPopulation(Room2Wolf));
        Assert.False(world.HasNeed(stale));
        Assert.Equal(WorldRules.StockTarget, world.GetStock(SmithSword)); // not saved -> default
    }
}
