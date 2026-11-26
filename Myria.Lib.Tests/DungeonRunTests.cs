using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems.Dungeons;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 dungeons: one party's copy of a dungeon (DungeonRun) over the real Hollow Crypt - room 64
/// is its entrance (west to cave room 21), 65 the next room, 72 the boss room (the lich, id 7).
/// </summary>
[Collection("GameData")]
public class DungeonRunTests
{
    public DungeonRunTests(GameDataFixture _) { }

    private static Room R(int id) => RoomService.AllRooms.First(r => r.Id == id);
    private static DungeonRun Crypt() => new("hollow_crypt");

    private static void ClearRoom(DungeonRun run, Room room)
    {
        foreach (var m in run.Remaining(room)) run.RecordKill(room.Id, m);
    }

    [Fact]
    public void ARoomsPack_IsRolledOnce_AndClearedByKillingEveryInstance()
    {
        var run = Crypt();
        var room = R(64);
        var pack = run.Remaining(room);

        Assert.Equal(DungeonRules.DefaultPackSize, pack.Count);
        Assert.Same(pack[0], run.Remaining(room)[0]);           // same instances on every look
        run.RecordKill(64, pack[0].Clone());                     // a lookalike isn't that monster
        Assert.Equal(pack.Count, run.Remaining(room).Count);

        ClearRoom(run, room);
        Assert.True(run.IsCleared(room));
        Assert.Contains(64, run.ClearedRoomIds);
    }

    [Fact]
    public void ABossRoom_HoldsJustItsBoss_AndAnExplicitPackIsUsedAsWritten()
    {
        Assert.Equal(7, Assert.Single(Crypt().Remaining(R(72))).Id);

        var custom = new Room("test", "test") { Id = 9999, IsDungeonRoom = true, DungeonPack = new() { [4] = 4, [5] = 2 } };
        var pack = Crypt().Remaining(custom);
        Assert.Equal(4, pack.Count(m => m.Id == 4));
        Assert.Equal(2, pack.Count(m => m.Id == 5));
    }

    [Fact]
    public void Packs_AreSplitAcrossFights_AndARunningFightsMonstersArentHandedOutTwice()
    {
        var run = Crypt();
        var room = new Room("test", "test") { Id = 9998, IsDungeonRoom = true, DungeonPack = new() { [4] = 5 } };
        bool firstActive = true;

        var first = run.NextPack(room, 3, () => firstActive);
        var second = run.NextPack(room, 3, () => true);

        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);                           // only what the first fight doesn't hold
        Assert.Empty(first.Intersect(second));

        firstActive = false;                                     // first fight ended (fled): its monsters are free again
        Assert.Equal(3, run.NextPack(room, 5, () => true).Count);
    }

    [Fact]
    public void Movement_GoesDeeperOnlyFromAClearedRoom_AndAlwaysAllowsFallingBack()
    {
        var run = Crypt();

        Assert.True(run.CanMove(R(21), R(64)));                  // the entrance, from outside
        Assert.False(run.CanMove(R(21), R(68)));                 // no skipping in
        Assert.False(run.CanMove(R(64), R(65)));                 // 64 still holds monsters
        Assert.True(run.CanMove(R(64), R(21)));                  // out is always fine

        ClearRoom(run, R(64));
        Assert.True(run.CanMove(R(64), R(65)));
        Assert.False(run.CanMove(R(65), R(66)));                 // 65 isn't cleared yet...
        Assert.True(run.CanMove(R(65), R(64)));                  // ...but back into cleared 64 is
    }
}
