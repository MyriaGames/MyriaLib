using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Systems.Enums;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myria.Lib.Core.Services
{
    public static class RoomService
    {
        public static List<Room> AllRooms { get; private set; } = new List<Room>();
        private static readonly string _defaultPath = "Data/common/rooms.json";

        /// <summary>
        /// Loads all rooms from the given path (default: Data/common/rooms.json).
        /// </summary>
        /// <returns>Room dictionary keyed by room ID.</returns>
        public static Dictionary<int, Room> LoadRooms(string path = "")
        {
            string filePath = string.IsNullOrEmpty(path) ? _defaultPath : path;

            if (!File.Exists(filePath))
                return new();

            string json = File.ReadAllText(filePath);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };

            var rooms = JsonSerializer.Deserialize<List<Room>>(json, options) ?? new();
            return LoadRooms(rooms);
        }

        /// <summary>Loads rooms from already-parsed data (e.g. read from a database) and resolves exits.</summary>
        public static Dictionary<int, Room> LoadRooms(List<Room> rooms)
        {
            AllRooms = rooms;

            // Create lookup map
            var roomMap = AllRooms.ToDictionary(r => r.Id, r => r);

            // Resolve exits
            foreach (var room in roomMap.Values)
            {
                foreach (var (direction, targetId) in room.ExitIds)
                {
                    if (roomMap.TryGetValue(targetId, out var targetRoom))
                        room.Exits[direction] = targetRoom;
                }
            }

            return roomMap;
        }
        /// <summary>
        /// gives back a room when given its id
        /// </summary>
        /// <param name="id">room id</param>
        /// <returns>room with that id</returns>
        public static Room GetRoomById(int id)
        {
            return AllRooms.FirstOrDefault(r => r.Id == id);
        }
        /// <summary>
        /// connects monsters to their saved rooms
        /// </summary>
        public static void ConnectMonsterRooms(List<Monster> monster, List<Room> rooms)
        {
            foreach (Monster mob in monster)
            {
                foreach (Room room in rooms)
                {
                    if (room.EncounterableMonsters.Keys.Contains(mob.Id))
                        room.Monsters.Add(mob);
                }

            }

        }
        public static bool CanEnterRoom(Room room, Character character)
        {
            switch (room.RequirementType)
            {
                case RoomRequirementType.None:
                    return true;
                case RoomRequirementType.Level:
                    return character.Level >= room.AccessLevel;
                case RoomRequirementType.Quest:
                    return !string.IsNullOrEmpty(room.RequiredQuestId)
                        && character.CompletedQuests.Any(q => q.Id == room.RequiredQuestId);
                case RoomRequirementType.Party:
                    // Intentional no-op, not a gap: this shared helper is only called from
                    // singleplayer navigation (Wpf's ViewModel_PageRoom, Mono's WorldDataService),
                    // and singleplayer has no party concept at all - there's nothing to check.
                    // Multiplayer's real party gate is separate and already enforced server-side
                    // in GameHub.JoinRoom (RoomRequirementType.Party => party.GetPartyId(...) is not null).
                    return true;
            }
            return true;
        }

    }

}