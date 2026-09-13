using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Mods;
using Myria.Lib.Core.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Myria.Lib.Core.Services
{
    public static class CharacterService
    {
        private static readonly JsonSerializerOptions _opts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new ItemConverter(), new MoneyConverter() }
        };

        private static readonly JsonSerializerOptions _snapshotOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        /// <summary>
        /// Bumped whenever the save file's JSON *shape* changes in a way a per-field JsonConverter
        /// can't express (renamed/restructured fields, etc). Field-level legacy-value conversions
        /// (e.g. Class/Race once stored as enum ints) are handled by their own JsonConverters and
        /// don't need a version bump. Saves written before this field existed are treated as
        /// version 0. See <see cref="MigrateCharacterJson"/> to add a new migration step.
        /// </summary>
        private const int CurrentSaveVersion = 1;

        private static string SavePath(UserAccount user, string characterName)
            => Path.Combine("Data/saves", $"{SafeFileName.Sanitize(user.Username)}-{SafeFileName.Sanitize(characterName)}.json");

        public static void DeleteCharacter(string name, UserAccount user)
        {
            string path = SavePath(user, name);
            if (File.Exists(path))
                File.Delete(path);
        }

        public static void SaveCharacter(UserAccount user, Character character)
        {
            string path = SavePath(user, character.Name);
            Directory.CreateDirectory("Data/saves");

            character.CurrentRoomId = character.CurrentRoom?.Id ?? character.CurrentRoomId;

            // Serialize character with its custom converters.
            string charJson = JsonSerializer.Serialize(character, _opts);

            // Wrap with the current mod snapshot so loading can detect drift.
            var snapshot = ModLoader.GetCurrentSnapshot();
            var wrapper  = new JsonObject
            {
                ["SaveVersion"]   = CurrentSaveVersion,
                ["SavedWithMods"] = JsonNode.Parse(JsonSerializer.Serialize(snapshot, _snapshotOpts)),
                ["Character"]     = JsonNode.Parse(charJson),
            };

            File.WriteAllText(path, wrapper.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Reads only the mod snapshot from a save file without loading the full character.
        /// Returns <c>null</c> if the file uses the old format (no snapshot stored).
        /// </summary>
        public static ModSnapshot? ReadModSnapshot(string name, UserAccount user)
        {
            string path = SavePath(user, name);
            if (!File.Exists(path)) return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("SavedWithMods", out var el)) return null;
                return JsonSerializer.Deserialize<ModSnapshot>(el.GetRawText(), _snapshotOpts);
            }
            catch { return null; }
        }

        public static Character? LoadCharacter(string name, UserAccount user)
        {
            string path = SavePath(user, name);
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllText(path);
            string characterJson = raw;

            // Support both the wrapper format ({ "SaveVersion":…, "SavedWithMods":…, "Character":… })
            // and the old bare-Character-JSON format (no wrapper at all - implicitly version 0).
            try
            {
                if (JsonNode.Parse(raw) is JsonObject root)
                {
                    var savedVersion = root.TryGetPropertyValue("SaveVersion", out var versionNode) && versionNode != null
                        ? versionNode.GetValue<int>()
                        : 0;

                    var characterNode = root.TryGetPropertyValue("Character", out var charNode) && charNode != null
                        ? charNode
                        : root;

                    characterJson = MigrateCharacterJson(characterNode, savedVersion).ToJsonString();
                }
            }
            catch { /* fall through and let Deserialize below report the real parse error */ }

            var character = JsonSerializer.Deserialize<Character>(characterJson, _opts);
            if (character == null) return null;

            character.Inventory.Items.RemoveAll(i => i == null);

            // Migrate legacy saves that stored Class/Race as enum integers (e.g. "Class": 3).
            MigrateLegacyClassRace(character);

            try
            {
                character.CurrentRoom = RoomService.AllRooms.FirstOrDefault(r => r.Id == character.CurrentRoomId);
            }
            catch { /* room reconnect handled by caller */ }

            character.RecalculateUnusedPoints();
            character.ValidateQuestStatuses();
            SkillFactory.UpdateSkills(character);
            ResolveAdvancedSystems(character);
            return character;
        }

        public static List<Character> LoadCharacters(UserAccount account)
        {
            var characters = new List<Character>();
            foreach (var name in account.CharacterNames)
            {
                var c = LoadCharacter(name, account);
                if (c != null) characters.Add(c);
            }
            return characters;
        }

        /// <summary>
        /// Applies structural JSON migrations needed to bring a save up to CurrentSaveVersion,
        /// before it's deserialized into Character. This is only for changes a JsonConverter can't
        /// express - renamed/removed/restructured fields - not legacy-value conversions (those
        /// belong on the property's own JsonConverter, e.g. CharacterClassJsonConverter).
        ///
        /// To add a migration: add "if (fromVersion < N) { ... mutate characterNode ... }" below,
        /// then bump CurrentSaveVersion to N.
        /// </summary>
        private static JsonNode MigrateCharacterJson(JsonNode characterNode, int fromVersion)
        {
            // No structural migrations exist yet - CurrentSaveVersion 1 just marks "this save
            // has a SaveVersion field at all". Add version-gated steps here as the save format
            // changes in the future.
            return characterNode;
        }

        /// <summary>
        /// Converts numeric keys in ClassXp that were saved as enum integers (old format) to
        /// string IDs. Character.Class/Race themselves are handled by CharacterClassJsonConverter/
        /// CharacterRaceJsonConverter at deserialize time.
        /// </summary>
        private static void MigrateLegacyClassRace(Character character)
        {
            // ClassXp — keys may be "3" etc.
            var legacyXpKeys = character.ClassXp.Keys.Where(k => int.TryParse(k, out _)).ToList();
            foreach (var key in legacyXpKeys)
            {
                if (int.TryParse(key, out var n)
                    && Myria.Lib.Core.Systems.Enums.CharacterClass.FromLegacyInt.TryGetValue(n, out var newKey))
                {
                    character.ClassXp[newKey] = character.ClassXp[key];
                    character.ClassXp.Remove(key);
                }
            }
        }

        private static void ResolveAdvancedSystems(Character character)
        {
            BaseRuneService.ResolveRunes(character);
            SkillSlotService.ResolveSlots(character);
            SkillSlotService.MigrateIfEmpty(character);
        }
    }
}
