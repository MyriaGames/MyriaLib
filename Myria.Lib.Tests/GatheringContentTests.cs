using System.Text.Json;
using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 gathering expansion: every gathering job has resources across the world, every gathered
/// resource is used by a recipe, spots can need a job skill level, and a room with several spots
/// yields all of them (gathering used to always take the first workable spot).
/// </summary>
[Collection("GameData")]
public class GatheringContentTests
{
    public GatheringContentTests(GameDataFixture _) { }

    private static IEnumerable<GatheringSpot> AllSpots => RoomService.AllRooms.SelectMany(r => r.GatheringSpots);

    private static HashSet<string> RecipeIngredients()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("Data", "common", "recipes.json")));
        return doc.RootElement.EnumerateObject()
            .SelectMany(npc => npc.Value.EnumerateArray())
            .SelectMany(r => r.GetProperty("Ingredients").EnumerateArray())
            .Select(i => i.GetProperty("ItemId").GetString()!)
            .ToHashSet();
    }

    [Theory]
    [InlineData(GatheringType.Ore, 3)]
    [InlineData(GatheringType.Tree, 3)]
    [InlineData(GatheringType.Herb, 5)]
    public void EachGatheringJob_HasSeveralResources_IncludingOneForBeginners(string type, int minResources)
    {
        var spots = AllSpots.Where(s => s.Type == type).ToList();
        Assert.True(spots.Select(s => s.GatheredItemId).Distinct().Count() >= minResources);
        Assert.Contains(spots, s => s.RequiredSkillLevel == 0);
    }

    [Fact]
    public void EverySpot_GivesARealItem_WithItsTool_AtAReachableLevel()
    {
        foreach (var spot in AllSpots)
        {
            Assert.True(ItemFactory.TryCreateItem(spot.GatheredItemId, out _), $"{spot.Id} gives unknown {spot.GatheredItemId}");
            if (!string.IsNullOrEmpty(spot.RequiredToolId))
                Assert.True(ItemFactory.TryCreateItem(spot.RequiredToolId, out _), $"{spot.Id} needs unknown tool {spot.RequiredToolId}");
            // Gathering grants 10 skill XP (15 for the active job): level 5 is already ~670 gathers.
            Assert.InRange(spot.RequiredSkillLevel, 0, 5);
        }
    }

    [Fact]
    public void EveryGatheredResource_IsUsedByARecipe()
    {
        var used = RecipeIngredients();
        foreach (var item in AllSpots.Select(s => s.GatheredItemId).Distinct())
            Assert.True(used.Contains(item), $"{item} can be gathered but nothing uses it");
    }

    // ── Picking a spot ────────────────────────────────────────────────────

    private static Room RoomWith(params GatheringSpot[] spots)
    {
        var room = new Room("Test", "") { GatheringSpots = spots.ToList() };
        room.RollGatherLimit();
        return room;
    }

    private static GatheringSpot Herb(string id, string item, int level = 0) =>
        new() { Id = id, Name = id, Description = "", Type = GatheringType.Herb, GatheredItemId = item, RequiredSkillLevel = level };

    [Fact]
    public void ARoomWithSeveralSpots_YieldsAllOfThem()
    {
        var character = TestHelpers.CreateCharacter();
        var room = RoomWith(Herb("a", "sunleaf"), Herb("b", "marshroot"));

        var picked = Enumerable.Range(0, 200).Select(i => GatherService.PickSpot(character, room, out _)!.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "a", "b" }, picked);
    }

    [Fact]
    public void ASpotAboveTheCharactersSkill_IsSkipped_OrReportedAsSkillTooLow()
    {
        var character = TestHelpers.CreateCharacter();
        var hard = Herb("hard", "frostbloom", level: 5);

        Assert.Null(GatherService.PickSpot(character, RoomWith(hard), out var failure));
        Assert.Equal(GatherResult.SkillTooLow, failure);
        Assert.Equal(GatherResult.SkillTooLow, GatherService.Gather(character, RoomWith(hard)).Result);
        Assert.All(Enumerable.Range(0, 50), i =>
            Assert.Equal("easy", GatherService.PickSpot(character, RoomWith(hard, Herb("easy", "sunleaf")), out _)!.Id));

        JobManager.GetOrAdd(character, "herbalist").SkillXp = JobXpService.TotalXpToReach(5);
        Assert.Equal("frostbloom", GatherService.Gather(character, RoomWith(hard)).ItemId);
    }
}
