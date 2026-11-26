using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Entities.Maps
{
    public class Room
    {
        public int Id { get; set; } = 0;
        public int DungonId { get; set; } = -1;
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool HasMonsters { get; set; }
        public RoomRequirementType RequirementType { get; set; } = RoomRequirementType.None;
        public int AccessLevel { get; set; } = 0;
        public string? RequiredQuestId { get; set; } = null;
        public bool IsDungeonRoom { get; set; } = false;
        public bool IsBossRoom { get; set; } = false;
        public bool IsCaveRoom { get; set; } = false;
        public bool IsCity { get; set; } = false;
        /// <summary>PvP is only ever possible in rooms flagged like this (v0.3: no world PvP) -
        /// enforced by the server (GameHub.StartPvpFight).</summary>
        public bool IsPvpRoom { get; set; } = false;
        public bool IsCleared { get; set; } = false; // unlocked once all dungeon monsters are defeated
        /// <summary>When true, Name and Description are display strings, not locale keys.</summary>
        public bool IsLiteral { get; set; } = false;
        public int DailyGatherLimit { get; set; } = 0;
        public int GathersRemaining { get; set; } = 0;
        public List<GatheringSpot> GatheringSpots { get; set; } = new();
        public List<string> Npcs { get; set; } = new(); // e.g. ["Healer", "Smith"]
        public Dictionary<string, int> ExitIds { get; set; } = new();
        public Dictionary<string, Room> Exits { get; set; } = new();     // populated after loading
        public Dictionary<int, float> EncounterableMonsters { get; set; } = new();

        /// <summary>
        /// Monster templates for this room — loaded once at startup by RoomService.ConnectMonsterRooms.
        /// Used as the source for random encounters (overworld) or dungeon spawns.
        /// Do not mutate these; use <see cref="SpawnDungeonMonsters"/> for dungeon combat.
        /// </summary>
        public List<Monster> Monsters { get; set; } = new();

        /// <summary>
        /// Active monster instances currently alive in this room.
        /// Only populated in dungeon rooms via <see cref="SpawnDungeonMonsters"/>.
        /// When this list reaches zero the room becomes <see cref="IsCleared"/>.
        /// </summary>
        public List<Monster> CurrentMonsters { get; set; } = new();

        public List<Corpse> Corpses { get; set; } = new();
        public List<Npc> NpcRefs { get; set; } = new();

        public Room(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public void ConnectRoom(string direction, Room room)
        {
            Exits[direction] = room;
        }

        /// <summary>Rolls the daily gather limit for this room (1–5).</summary>
        public void RollGatherLimit()
        {
            DailyGatherLimit = Random.Shared.Next(1, 6);
            GathersRemaining = DailyGatherLimit;
        }

        /// <summary>Adds extra gather charges on top of the rolled daily limit (J18 — knowledge bonus).</summary>
        public void AddGatherBonus(int bonus)
        {
            if (bonus > 0) GathersRemaining += bonus;
        }

        /// <summary>
        /// Spawns fresh monster instances into <see cref="CurrentMonsters"/> from the room's
        /// <see cref="Monsters"/> templates. Call this when the player enters a dungeon room.
        /// </summary>
        public void SpawnDungeonMonsters()
        {
            if (!IsDungeonRoom) return;
            CurrentMonsters.Clear();
            foreach (var template in Monsters)
                CurrentMonsters.Add(template.Clone());
        }

        /// <summary>
        /// Attempts to consume one gather action from this room.
        /// Returns the chosen <see cref="GatheringSpot"/> via <paramref name="spot"/> on success,
        /// or null on failure. Item creation and inventory logic live in GatherService.
        /// </summary>
        public GatherResult TryConsumeGather(out GatheringSpot? spot)
        {
            spot = null;
            if (GatheringSpots.Count == 0) return GatherResult.NoSpots;
            if (GathersRemaining <= 0)     return GatherResult.Depleted;

            spot = GatheringSpots[Random.Shared.Next(GatheringSpots.Count)];
            GathersRemaining--;
            return GatherResult.Success;
        }
    }
}
