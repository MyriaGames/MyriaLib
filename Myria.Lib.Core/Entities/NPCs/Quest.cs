using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Entities.NPCs
{
    public class Quest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        public string GiverNpcId  { get; set; } = "";
        /// <summary>NPC to return the quest to. Null means same as <see cref="GiverNpcId"/>.</summary>
        public string? ReturnNpcId { get; set; }
        public string ReturnNpcIdResolved => string.IsNullOrEmpty(ReturnNpcId) ? GiverNpcId : ReturnNpcId;

        /// <summary>Dialog shown when the player first accepts this quest (read through to confirm accept).</summary>
        public List<DialogLine> AcceptDialog { get; set; } = new();
        /// <summary>Dialog shown when the player returns a completed quest.</summary>
        public List<DialogLine> ReturnDialog { get; set; } = new();

        public int RequiredLevel { get; set; } = 1;
        public QuestStatus Status { get; set; } = QuestStatus.NotStarted;

        /// <summary>Section this quest is grouped under in the quest list UI (Q14). Defaults to
        /// Side so existing/mod-supplied quest data that omits this field still groups sensibly
        /// without needing every entry updated.</summary>
        public QuestCategory Category { get; set; } = QuestCategory.Side;

        /// <summary>True when the quest has no kill or item objectives; auto-completes on accept.</summary>
        public bool IsTalkOnly => RequiredKills.Count == 0 && RequiredItems.Count == 0;

        public List<string> PrerequisiteQuestIds { get; set; } = new(); // quest IDs that must be completed first

        // Repeatable config (defined in JSON, read-only after load)
        public bool IsRepeatable { get; set; } = false;
        public int RepeatMaxLevel { get; set; } = 0;    // 0 = no cap; always enforced on re-accept
        public int RepeatDailyLimit { get; set; } = 0;  // 0 = no daily limit
        public int RepeatTotalLimit { get; set; } = 0;  // 0 = no total limit
        public Dictionary<int, int> RequiredKills { get; set; } = new(); // monsterId => amount
        public Dictionary<int, int> KillProgress { get; set; } = new();  // monsterId => current count
        public Dictionary<string, int> RequiredItems { get; set; } = new();   // itemId => amount
        public Dictionary<string, int> ItemProgress { get; set; } = new();    // itemId => how many the player has

        /// <summary>Items added to the player's inventory immediately on quest accept.</summary>
        public List<string> AcceptItems { get; set; } = new();

        public int RewardXp { get; set; }
        public int RewardGold { get; set; }
        public List<string> RewardItems { get; set; } = new();

        // ── Class / Race requirements (Q12, Q13) ────────────────────────────────
        /// <summary>Character must be this class ID to see or accept this quest. Null = no restriction.</summary>
        public string? RequiredClass { get; set; }
        /// <summary>Character must be this race ID to see or accept this quest. Null = no restriction.</summary>
        public string? RequiredRace { get; set; }

        // ── Job aspect level requirements (Q11) ─────────────────────────────────
        /// <summary>Which job's aspect levels are checked. Null = no restriction.</summary>
        public string? RequiredAspectJobId    { get; set; }
        /// <summary>Minimum Skill level required in <see cref="RequiredAspectJobId"/>. 0 = no requirement.</summary>
        public int     RequiredSkillLevel     { get; set; } = 0;
        /// <summary>Minimum Knowledge level required in <see cref="RequiredAspectJobId"/>. 0 = no requirement.</summary>
        public int     RequiredKnowledgeLevel { get; set; } = 0;
        /// <summary>Minimum Fame level required in <see cref="RequiredAspectJobId"/>. 0 = no requirement.</summary>
        public int     RequiredFameLevel      { get; set; } = 0;

        // ── Party gating (Q10) ───────────────────────────────────────────────────
        /// <summary>When true the player must be in a party to accept this quest.</summary>
        public bool RequiresParty { get; set; } = false;
        /// <summary>Minimum party size required. 0 = any party size is fine (just must be in one).</summary>
        public int RequiredPartySize { get; set; } = 0;

        // ── Job gating (J7) ──────────────────────────────────────────────────────
        /// <summary>Character must have this job active to see or accept this quest. Null = no restriction.</summary>
        public string? RequiredActiveJobId { get; set; }

        // ── Job aspect rewards on return (J9, J10) ───────────────────────────────
        public string? JobKnowledgeRewardJobId  { get; set; }
        public long    JobKnowledgeRewardAmount  { get; set; }
        public string? JobFameRewardJobId        { get; set; }
        public long    JobFameRewardAmount       { get; set; }

        // ── Dynamic quests (v0.3 quest rework) ───────────────────────────────────
        /// <summary>Generated from the world state (an NPC running low, a room overrun) rather than
        /// authored in quests.json; its Id is "dyn.&lt;grantId&gt;" and it isn't repeatable or saved as completed.</summary>
        public bool IsDynamic { get; set; }
        /// <summary>Kill objectives only count in this room (dynamic kill quests are about one room's population).</summary>
        public int? RequiredRoomId { get; set; }
        /// <summary>Can only be handed in at <see cref="ReturnNpcIdResolved"/> in this room (NPC ids are
        /// templates reused across rooms; a fetch quest belongs to one NPC instance).</summary>
        public int? ReturnRoomId { get; set; }
        /// <summary>Format arguments for <see cref="Name"/>/<see cref="Description"/> when those are
        /// localization keys with placeholders (dynamic quests). Args that are themselves keys
        /// (item./monster./room./game.npc.) are localized too - see QuestText.</summary>
        public List<string> NameArgs { get; set; } = new();
        public List<string> DescriptionArgs { get; set; } = new();

        /// <summary>Returns a fresh copy of the quest template ready to be accepted.</summary>
        public Quest Clone() => new Quest
        {
            Id = Id,
            Name = Name,
            Description = Description,
            GiverNpcId  = GiverNpcId,
            ReturnNpcId = ReturnNpcId,
            AcceptDialog = AcceptDialog,  // read-only game data; shared reference is fine
            ReturnDialog = ReturnDialog,
            RequiredLevel = RequiredLevel,
            Status = QuestStatus.NotStarted,
            Category = Category,
            RequiredKills = new Dictionary<int, int>(RequiredKills),
            KillProgress = new Dictionary<int, int>(),
            RequiredItems = new Dictionary<string, int>(RequiredItems),
            ItemProgress = new Dictionary<string, int>(),
            AcceptItems = new List<string>(AcceptItems),
            RewardXp = RewardXp,
            RewardGold = RewardGold,
            RewardItems = new List<string>(RewardItems),
            PrerequisiteQuestIds = new List<string>(PrerequisiteQuestIds),
            IsRepeatable = IsRepeatable,
            RepeatMaxLevel = RepeatMaxLevel,
            RepeatDailyLimit = RepeatDailyLimit,
            RepeatTotalLimit = RepeatTotalLimit,
            RequiredClass            = RequiredClass,
            RequiredRace             = RequiredRace,
            RequiredAspectJobId      = RequiredAspectJobId,
            RequiredSkillLevel       = RequiredSkillLevel,
            RequiredKnowledgeLevel   = RequiredKnowledgeLevel,
            RequiredFameLevel        = RequiredFameLevel,
            RequiresParty            = RequiresParty,
            RequiredPartySize        = RequiredPartySize,
            RequiredActiveJobId      = RequiredActiveJobId,
            JobKnowledgeRewardJobId  = JobKnowledgeRewardJobId,
            JobKnowledgeRewardAmount = JobKnowledgeRewardAmount,
            JobFameRewardJobId       = JobFameRewardJobId,
            JobFameRewardAmount      = JobFameRewardAmount,
            IsDynamic                = IsDynamic,
            RequiredRoomId           = RequiredRoomId,
            ReturnRoomId             = ReturnRoomId,
            NameArgs                 = new List<string>(NameArgs),
            DescriptionArgs          = new List<string>(DescriptionArgs),
        };

        /// <summary>Grants any items listed in <see cref="AcceptItems"/> to the player's inventory.</summary>
        public void GrantAcceptItems(Character character)
        {
            foreach (var itemId in AcceptItems)
                if (ItemFactory.TryCreateItem(itemId, out var item))
                    character.Inventory.AddItem(item, character);
        }

        public void GrantRewards(Character character)
        {
            character.GainXp(RewardXp);

            if (RewardGold > 0)
                character.Money.TryAdd(RewardGold);

            foreach (var itemId in RewardItems)
                if (ItemFactory.TryCreateItem(itemId, out var item))
                    character.Inventory.AddItem(item, character);

            // J9: Knowledge XP reward
            if (!string.IsNullOrEmpty(JobKnowledgeRewardJobId) && JobKnowledgeRewardAmount > 0)
                JobManager.GrantKnowledgeXp(character, JobKnowledgeRewardJobId, JobKnowledgeRewardAmount);

            // J10: Fame XP reward (GrantFameXp enforces active-job requirement internally)
            if (!string.IsNullOrEmpty(JobFameRewardJobId) && JobFameRewardAmount > 0)
                JobManager.GrantFameXp(character, JobFameRewardJobId, JobFameRewardAmount);
        }

    }

}
