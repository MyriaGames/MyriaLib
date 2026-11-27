using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems.Dungeons;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Dungeon packs scale with the party (v0.3): monster HP linearly per extra member, damage with
/// the square of it - a solo run is unchanged.
/// </summary>
[Collection("GameData")]
public class DungeonPartyScalingTests
{
    public DungeonPartyScalingTests(GameDataFixture _) { }

    private const int GrottoRoom = 74;

    [Fact]
    public void Scales_AreOneSolo_AndGrowWithTheParty()
    {
        Assert.Equal(1f, DungeonRules.PartyHealthScale(1));
        Assert.Equal(1f, DungeonRules.PartyDamageScale(1));
        Assert.Equal(1.3f, DungeonRules.PartyHealthScale(2), 3);
        Assert.Equal(2.2f, DungeonRules.PartyHealthScale(5), 3);
        Assert.Equal(1.035f, DungeonRules.PartyDamageScale(2), 3);
        Assert.Equal(1.56f, DungeonRules.PartyDamageScale(5), 3);
    }

    [Fact]
    public void SpawnPack_ForAParty_HasScaledMonsters_SoloDoesNot()
    {
        var room = RoomService.GetRoomById(GrottoRoom);
        var solo = DungeonRun.SpawnPack(room);
        var trio = DungeonRun.SpawnPack(room, 3);

        Assert.Equal(solo.Count, trio.Count);   // same monsters, tougher
        Assert.All(solo, m => { Assert.Equal(1f, m.HealthScale); Assert.Equal(1f, m.DamageScale); });
        for (int i = 0; i < solo.Count; i++)
        {
            Assert.Equal(DungeonRules.PartyDamageScale(3), trio[i].DamageScale);
            Assert.InRange(trio[i].MaxHealth, (int)(solo[i].MaxHealth * 1.59), (int)(solo[i].MaxHealth * 1.61) + 1);
            Assert.Equal(trio[i].MaxHealth, trio[i].CurrentHealth);
        }
    }

    [Fact]
    public void Run_RollsPacksAtItsPartySize_AndClonesKeepTheScale()
    {
        var room = RoomService.GetRoomById(GrottoRoom);
        var run = new DungeonRun("sunken_grotto") { PartySize = 4 };

        var pack = run.Remaining(room);
        Assert.All(pack, m => Assert.Equal(DungeonRules.PartyHealthScale(4), m.HealthScale));
        Assert.Equal(DungeonRules.PartyDamageScale(4), pack[0].Clone().DamageScale);
    }
}
