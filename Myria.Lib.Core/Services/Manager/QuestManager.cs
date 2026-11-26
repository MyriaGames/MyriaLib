using System.Text.Json;
using System.Text.Json.Serialization;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Services.Manager
{
    public static class QuestManager
    {
        private static List<Quest> _allQuests = new();

        /// <summary>
        /// Event-handler adapter for <see cref="Myria.Lib.Core.Systems.GameEvents.ItemReceived"/>.
        /// Wired explicitly in GameService.InitializeGame() — NOT in a static constructor: a type
        /// initializer only runs once something actually touches QuestManager, which nothing on
        /// the Inventory/GameEvents call path does, so a static-constructor subscription here
        /// silently never fires until some unrelated code happens to touch QuestManager first.
        /// A named method (not a lambda) is required so the explicit -=/+= at the call site can
        /// de-duplicate across repeated InitializeGame() calls (each lambda would be a distinct
        /// delegate instance and -= wouldn't remove the previous one).
        /// </summary>
        public static void OnItemReceived(Character character, Myria.Lib.Core.Entities.Items.Item item, int amount)
            => UpdateItemProgress(character);

        /// <summary>Event-handler adapter for <see cref="Myria.Lib.Core.Systems.GameEvents.MonsterKilled"/>. See <see cref="OnItemReceived"/> for why this isn't a static-constructor subscription.</summary>
        public static void OnMonsterKilled(Character character, Myria.Lib.Core.Entities.Monsters.Monster monster)
            => UpdateKillProgress(character, monster.Id);

        /// <summary>
        /// Recomputes every in-progress quest's item-collection progress from the character's
        /// current inventory and completes any quest whose kill and item objectives are both met.
        /// Call after inventory contents change in a way that could satisfy a quest (normally via
        /// <see cref="Myria.Lib.Core.Systems.GameEvents.ItemReceived"/> — this is wired automatically).
        /// </summary>
        public static void UpdateItemProgress(Character character)
        {
            // Completed quests too: items sold, traded away or used up after the counter was full must
            // count down again (and the quest go back to in progress) - item progress is "how many you
            // have", not "how many you ever picked up".
            foreach (var quest in character.ActiveQuests.Where(q => q.Status is QuestStatus.InProgress or QuestStatus.Completed))
            {
                foreach (var itemReq in quest.RequiredItems)
                {
                    int owned = character.Inventory.Items.Where(i => i.Id == itemReq.Key).Sum(i => i.StackSize);
                    quest.ItemProgress[itemReq.Key] = Math.Min(owned, itemReq.Value);
                }

                RefreshStatus(quest);
            }
        }

        /// <summary>
        /// Starts <paramref name="template"/> for <paramref name="character"/>: adds a fresh copy to the active
        /// quests, hands out its accept items and counts items the character already carries. The one way to
        /// accept a quest - on the server and in the clients - so a quest accepted with the items already in
        /// the bag doesn't show 0/N until something new is picked up.
        /// </summary>
        public static Quest StartQuest(Character character, Quest template)
        {
            var clone = template.Clone();
            clone.Status = clone.IsTalkOnly ? QuestStatus.Completed : QuestStatus.InProgress;
            character.ActiveQuests.Add(clone);
            clone.GrantAcceptItems(character);
            UpdateItemProgress(character);
            return clone;
        }

        /// <summary>
        /// Credits a monster kill toward every in-progress quest of <paramref name="character"/>
        /// that requires it, and completes any quest whose kill and item objectives are both met.
        /// Wired automatically via <see cref="Myria.Lib.Core.Systems.GameEvents.MonsterKilled"/> — call
        /// this once per character who should receive credit (e.g. every living party member).
        /// </summary>
        public static void UpdateKillProgress(Character character, int monsterId)
        {
            foreach (var quest in character.ActiveQuests.Where(q => q.Status == QuestStatus.InProgress))
            {
                if (!quest.RequiredKills.TryGetValue(monsterId, out int required))
                    continue;

                if (!quest.KillProgress.ContainsKey(monsterId))
                    quest.KillProgress[monsterId] = 0;

                if (quest.KillProgress[monsterId] >= required)
                    continue;

                quest.KillProgress[monsterId]++;
                TryCompleteQuest(quest);
            }
        }

        /// <summary>
        /// Completes a quest only once BOTH its kill and item objectives are satisfied. Previously
        /// CombatEncounter/GroupCombatEncounter each completed a quest as soon as kills were done,
        /// ignoring any still-unmet item requirements — centralizing here fixes that divergence.
        /// </summary>
        private static void TryCompleteQuest(Quest quest)
        {
            if (ObjectivesMet(quest))
                quest.Status = QuestStatus.Completed;
        }

        /// <summary>True when every kill and item objective of <paramref name="quest"/> is met.</summary>
        public static bool ObjectivesMet(Quest quest) =>
            quest.RequiredKills.All(rk => quest.KillProgress.TryGetValue(rk.Key, out int kills) && kills >= rk.Value) &&
            quest.RequiredItems.All(ri => quest.ItemProgress.TryGetValue(ri.Key, out int items) && items >= ri.Value);

        /// <summary>
        /// Sets an active quest's status from its counters - Completed when every objective is met, back to
        /// InProgress when not. The clients take the server's counters (Character.ApplySyncedQuestProgress);
        /// without this the status stayed what it was, so a quest showing 5/5 still couldn't be handed in.
        /// Returned/not-started quests are left alone.
        /// </summary>
        public static void RefreshStatus(Quest quest)
        {
            if (quest.Status is QuestStatus.InProgress or QuestStatus.Completed)
                quest.Status = ObjectivesMet(quest) ? QuestStatus.Completed : QuestStatus.InProgress;
        }

        public static void LoadQuests(string path = "Data/common/quests.json")
        {
            var json = File.ReadAllText(path);
            // Category (Q14) is the only enum quests.json ever populates - RoomService.LoadRooms
            // uses the identical JsonStringEnumConverter(CamelCase) pattern for RoomRequirementType,
            // its own closed-set JSON-facing enum.
            var options = new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            var quests = JsonSerializer.Deserialize<List<Quest>>(json, options)!;
            LoadQuests(quests);
        }

        /// <summary>Loads quests from already-parsed data (e.g. read from a database).</summary>
        public static void LoadQuests(List<Quest> quests)
        {
            _allQuests = quests;
        }
        public static List<Quest> GetAvailableForCharacter(Character character, int partySize = 1)
        {
            return _allQuests
                .Where(q => character.Level >= q.RequiredLevel && CanAccept(q, character, partySize))
                .ToList();
        }

        /// <summary>
        /// Every condition for accepting <paramref name="q"/> (level, prerequisites, not already done /
        /// repeat limits, class, race, job, party). The same rules the quest lists use - and what the
        /// realm server must check before accepting, which it didn't: completed quests could be
        /// accepted and handed in again endlessly (security review 2026-11-26, A06 F5).
        /// </summary>
        public static bool IsAcceptable(Quest q, Character character, int partySize = 1)
            => character.Level >= q.RequiredLevel && CanAccept(q, character, partySize);

        private static bool CanAccept(Quest q, Character character, int partySize = 1)
        {
            // Already accepted and in progress
            if (character.ActiveQuests.Any(aq => aq.Id == q.Id))
                return false;

            // Q10: Party requirement
            if (q.RequiresParty && partySize < 2)
                return false;
            if (q.RequiredPartySize > 0 && partySize < q.RequiredPartySize)
                return false;

            // Q12: Class requirement
            if (!string.IsNullOrEmpty(q.RequiredClass)
                && !q.RequiredClass.Equals(character.Class, StringComparison.OrdinalIgnoreCase))
                return false;

            // Q13: Race requirement
            if (!string.IsNullOrEmpty(q.RequiredRace)
                && !q.RequiredRace.Equals(character.Race, StringComparison.OrdinalIgnoreCase))
                return false;

            // Q11: Job aspect level requirements
            if (!string.IsNullOrEmpty(q.RequiredAspectJobId))
            {
                var entry = JobManager.GetOrAdd(character, q.RequiredAspectJobId);
                if (q.RequiredSkillLevel     > 0 && JobXpService.GetLevel(entry.SkillXp)     < q.RequiredSkillLevel)     return false;
                if (q.RequiredKnowledgeLevel > 0 && JobXpService.GetLevel(entry.KnowledgeXp) < q.RequiredKnowledgeLevel) return false;
                if (q.RequiredFameLevel      > 0 && JobXpService.GetLevel(entry.FameXp)      < q.RequiredFameLevel)      return false;
            }

            // J8: Require a specific active job
            if (!string.IsNullOrEmpty(q.RequiredActiveJobId) && character.ActiveJobId != q.RequiredActiveJobId)
                return false;

            // All prerequisite quests must have been completed at least once
            foreach (var prereqId in q.PrerequisiteQuestIds)
            {
                bool done = character.CompletedQuests.Any(cq => cq.Id == prereqId)
                    || (character.RepeatableQuestRecords.TryGetValue(prereqId, out var rec) && rec.TimesCompleted > 0);
                if (!done) return false;
            }

            if (!q.IsRepeatable)
                return !character.CompletedQuests.Any(cq => cq.Id == q.Id);

            // Level cap always enforced on re-accept
            if (q.RepeatMaxLevel > 0 && character.Level > q.RepeatMaxLevel)
                return false;

            character.RepeatableQuestRecords.TryGetValue(q.Id, out var record);

            // Total completion limit
            if (q.RepeatTotalLimit > 0 && record != null && record.TimesCompleted >= q.RepeatTotalLimit)
                return false;

            // Daily limit
            if (q.RepeatDailyLimit > 0 && record != null &&
                record.LastCompletionDate?.Date == DateTime.Today &&
                record.CompletionsToday >= q.RepeatDailyLimit)
                return false;

            return true;
        }

        public static Quest? GetQuestById(string id) =>
            _allQuests.FirstOrDefault(q => q.Id == id);

        /// <summary>Quests this NPC can give that the player is currently eligible to accept.</summary>
        public static List<Quest> GetAcceptableForNpc(Character character, string npcId, int partySize = 1)
            => _allQuests
                .Where(q => q.GiverNpcId == npcId && character.Level >= q.RequiredLevel && CanAccept(q, character, partySize))
                .ToList();

        /// <summary>Active completed quests the player can return to this NPC.</summary>
        public static List<Quest> GetReturnableForNpc(Character character, string npcId)
        {
            UpdateItemProgress(character);   // what's in the bag now decides, not what was picked up once
            return character.ActiveQuests
                .Where(q => q.Status == QuestStatus.Completed && q.ReturnNpcIdResolved == npcId)
                .ToList();
        }
    }

}
