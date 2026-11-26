using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Regestries;

namespace Myria.Lib.Core.Systems.Dungeons
{
    /// <summary>Dungeon tuning (v0.3). Placeholders, like the other v0.3 rule sets.</summary>
    public static class DungeonRules
    {
        /// <summary>Most monsters of a room's pack one fight takes on, solo / as a party. A bigger
        /// pack takes several fights.</summary>
        public const int SoloPackPerFight  = 3;
        public const int PartyPackPerFight = 5;

        /// <summary>Pack size for a normal dungeon room with no <see cref="Room.DungeonPack"/>.</summary>
        public const int DefaultPackSize = 3;

        public static int PackPerFight(bool party) => party ? PartyPackPerFight : SoloPackPerFight;

        /// <summary>The dungeon a room belongs to, or null for any non-dungeon room.</summary>
        public static string? DungeonIdOf(Room? room) =>
            room is { IsDungeonRoom: true } ? DungeonRegistry.GetDungeonByRoom(room)?.Id : null;
    }

    /// <summary>
    /// One party's (or solo player's) copy of a dungeon (v0.3): which monsters are still alive in
    /// each room and which rooms are cleared. A room's pack is rolled the first time it's looked
    /// at, so entering decides what's there; it's fought in one or more pack fights (see
    /// <see cref="DungeonRules"/>), and the room counts as cleared once the pack is dead.
    /// <para>
    /// Movement: you go deeper only from a cleared room (or from outside, through the entrance);
    /// from a room still holding monsters you can only fall back - to a cleared room or out.
    /// The server keeps one per party/player and drops it when everyone has left; offline clients
    /// keep one locally. Thread-safe (party members act on the same run).
    /// </para>
    /// </summary>
    public class DungeonRun
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, List<Monster>> _remaining = new();
        private readonly List<(List<Monster> Monsters, Func<bool> IsActive)> _engaged = new();

        public string DungeonId { get; }

        public DungeonRun(string dungeonId) => DungeonId = dungeonId;

        /// <summary>Monsters still alive in <paramref name="room"/> (rolls its pack on first look).</summary>
        public IReadOnlyList<Monster> Remaining(Room room)
        {
            lock (_sync) return RemainingLocked(room).ToList();
        }

        public bool IsCleared(Room room)
        {
            lock (_sync) return RemainingLocked(room).Count == 0;
        }

        /// <summary>Rooms of this run already cleared (only rooms that were entered count).</summary>
        public IReadOnlyList<int> ClearedRoomIds
        {
            get { lock (_sync) return _remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList(); }
        }

        /// <summary>
        /// The next pack to fight in <paramref name="room"/>: up to <paramref name="max"/> live monsters
        /// not already in another running fight. <paramref name="isActive"/> says whether that fight
        /// is still going; until it isn't, its monsters aren't handed out again. Empty if nothing's left
        /// (or everything left is already being fought).
        /// </summary>
        public List<Monster> NextPack(Room room, int max, Func<bool> isActive)
        {
            lock (_sync)
            {
                _engaged.RemoveAll(e => !e.IsActive());
                var busy = _engaged.SelectMany(e => e.Monsters).ToHashSet(ReferenceEqualityComparer.Instance);
                var pack = RemainingLocked(room).Where(m => !busy.Contains(m)).Take(max).ToList();
                if (pack.Count > 0) _engaged.Add((pack, isActive));
                return pack;
            }
        }

        /// <summary>A monster of this run died (by instance - two of the same type are two monsters).</summary>
        public void RecordKill(int roomId, Monster monster)
        {
            lock (_sync)
                if (_remaining.TryGetValue(roomId, out var list))
                    list.RemoveAll(m => ReferenceEquals(m, monster));
        }

        /// <summary>
        /// Whether a move from <paramref name="from"/> (null: no known previous room) into
        /// <paramref name="to"/> is allowed by this run. Leaving the dungeon is always allowed, and so
        /// is falling back into a cleared room; going into a room still holding monsters needs an
        /// exit from a cleared room - or from outside, which is the entrance.
        /// </summary>
        public bool CanMove(Room? from, Room to)
        {
            if (!to.IsDungeonRoom) return true;
            lock (_sync)
            {
                if (_remaining.TryGetValue(to.Id, out var there) && there.Count == 0) return true;
                return AdjacentFromClear(from, to);
            }
        }

        private bool AdjacentFromClear(Room? from, Room to)
        {
            if (from is null || !from.ExitIds.ContainsValue(to.Id)) return false;
            return !from.IsDungeonRoom || RemainingLocked(from).Count == 0;
        }

        private List<Monster> RemainingLocked(Room room)
        {
            if (!_remaining.TryGetValue(room.Id, out var list))
                _remaining[room.Id] = list = SpawnPack(room);
            return list;
        }

        /// <summary>Fresh instances for a room's pack (see <see cref="Room.DungeonPack"/>).</summary>
        public static List<Monster> SpawnPack(Room room)
        {
            var pack = new List<Monster>();
            if (!room.IsDungeonRoom || (room.Monsters.Count == 0 && room.DungeonPack.Count == 0)) return pack;

            if (room.DungeonPack.Count > 0)
            {
                foreach (var (id, count) in room.DungeonPack)
                {
                    var template = room.Monsters.FirstOrDefault(m => m.Id == id) ?? MonsterService.GetMonsterById(id);
                    if (template is null) continue;
                    for (int i = 0; i < count; i++) pack.Add(template.Clone());
                }
                return pack;
            }

            if (room.Monsters.Count == 0) return pack;
            if (room.IsBossRoom)
                return room.Monsters.Select(m => m.Clone()).ToList();

            for (int i = 0; i < DungeonRules.DefaultPackSize; i++)
                pack.Add(MonsterService.PickMonsterForFight(room.Monsters, room.EncounterableMonsters).Clone());
            return pack;
        }
    }
}
