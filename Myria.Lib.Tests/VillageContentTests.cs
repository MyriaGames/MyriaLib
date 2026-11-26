using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Regestries;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.World;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 villages: small settlements on the long roads, each a safe room with a healer, a trader and an
/// Adventurers' Guild house, reached by an exit from its road (and back).
/// </summary>
[Collection("GameData")]
public class VillageContentTests
{
    public VillageContentTests(GameDataFixture _) { }

    private static IEnumerable<Myria.Lib.Core.Entities.Maps.City> Villages => CityRegistry.GetAll().Where(c => c.IsVillage);

    [Fact]
    public void ThereAreVillages_OnTheLongRoads()
    {
        Assert.Equal(3, Villages.Count());
    }

    [Fact]
    public void EachVillage_IsASafeRoom_WithAHealerATraderAndAGuildHouse()
    {
        foreach (var village in Villages)
        {
            var room = RoomService.GetRoomById(village.AnchorRoomId)!;
            Assert.True(room.IsCity, $"{village.Id} isn't a safe room");
            Assert.Empty(room.EncounterableMonsters);
            var types = room.NpcRefs.Select(n => n.Type).ToList();
            Assert.Contains(NpcType.Healer, types);
            Assert.Contains(NpcType.Shop, types);
            Assert.Contains(NpcType.AdventurersGuild, types);
        }
    }

    [Fact]
    public void EachVillage_HangsOffItsRoad_BothWays()
    {
        foreach (var village in Villages)
        {
            var room = RoomService.GetRoomById(village.AnchorRoomId)!;
            var (_, roadId) = Assert.Single(room.ExitIds);
            var road = RoomService.GetRoomById(roadId)!;
            Assert.Contains(room.Id, road.ExitIds.Values);
            Assert.False(road.IsCity);                        // a road room, not another settlement
        }
    }

    [Fact]
    public void EachGuildHouse_CoversRoadRoomsWithMonsters()
    {
        foreach (var village in Villages)
        {
            var near = DynamicQuests.RoomsWithin(village.AnchorRoomId, DynamicQuestRules.GuildRadius);
            Assert.Contains(near, id => RoomService.GetRoomById(id)!.EncounterableMonsters.Count > 0);
        }
    }
}
