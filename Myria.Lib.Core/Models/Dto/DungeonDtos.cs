namespace Myria.Lib.Core.Models.Dto
{
    /// <summary>One monster still alive in a dungeon room (v0.3).</summary>
    public record DungeonMonsterState(int MonsterId, string Name, int Level);

    /// <summary>
    /// The caller's dungeon run as seen from their current room (v0.3): which rooms of it are
    /// cleared and what's still alive here. Null from the server outside dungeons.
    /// </summary>
    public record DungeonRoomState(string DungeonId, int RoomId, bool Cleared, List<int> ClearedRoomIds, List<DungeonMonsterState> Remaining);
}
