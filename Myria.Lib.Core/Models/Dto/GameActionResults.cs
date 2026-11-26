namespace Myria.Lib.Core.Models.Dto
{
    public record GatherActionResult(bool Success, string? Reason, string? ItemId, int Amount, long SkillXpGained, string? JobId, int RemainingGathers = -1);
    public record CraftActionResult(bool Success, string? Reason, string? ItemId, int Amount, long SkillXpGained, string? JobId);
    public record UpgradeActionResult(bool Success, string? Reason, string? ItemId, int UpgradeLevel, long SkillXpGained, string? JobId);
    public record StartCombatResult(bool Success, string? Reason, string? MonsterName, int MonsterHp, int MonsterMaxHp, int MonsterLevel = 0);
    public record NpcShopBuyResult(bool Success, string? Reason, long TotalCost, int Quantity);
    public record NpcSellResult(bool Success, string? Reason, long TotalGain, int Quantity);
    public record HealActionResult(bool Success, string? Reason, int CharacterHp, int CharacterMaxHp, int CharacterMana, int CharacterMaxMana);
    public record EquipItemResult(bool Success, string? Reason);
    public record CombatLogMessage(string Key, string[] Args);
    public record CombatTurnResult(
        bool Success,
        List<CombatLogMessage> LogEntries,
        int CharacterHp,
        int MonsterHp,
        string Phase,
        bool Finished,
        bool CharacterWon,
        long XpGained,
        List<string> LootItemIds,
        long RookieBonusXp = 0,
        int CharacterMana = 0,
        int CharacterMaxMana = 0);

    public record GroupCombatantState(string Name, int Hp, int MaxHp, bool IsAlive, int Level = 0, long XpGained = 0, long RookieBonusXp = 0, List<string>? LootItemIds = null, int Mana = 0, int MaxMana = 0);

    /// <summary>
    /// Authoritative character-level progression, fetched on demand after any action that
    /// might have changed it (combat win, etc.) instead of the client trying to replay the
    /// server's XP/level-up math itself, which drifts. Covers everything Character.LevelUp()
    /// mutates so a client behind by several levels catches up in one shot, not just Level/Xp.
    /// </summary>
    /// <summary>
    /// Authoritative kill/item objective progress for one active quest, fetched on demand
    /// after combat so the client's local Quest objects don't stay frozen at whatever they
    /// were when the character was last loaded — server-authoritative combat only ever
    /// ticks QuestManager's kill/item counters against the server's own session Character,
    /// never the client's local copy.
    /// </summary>
    public record QuestProgressState(
        string QuestId,
        Dictionary<int, int>    KillProgress,
        Dictionary<string, int> ItemProgress);

    public record CharacterProgressResult(
        int  Level,
        long Experience,
        long ExpForNextLvl,
        int  Strength,
        int  Dexterity,
        int  Endurance,
        int  Intelligence,
        int  Spirit,
        int  UnusedPoints,
        int  BaseHealth,
        int  BaseMana);
    /// <summary>One inventory stack. Equipment also carries its upgrade level and craft quality
    /// (defaults for everything else) - pieces don't stack, and an upgraded one must not come back
    /// as a plain copy. The defaults keep older clients/servers compatible.</summary>
    public record InventoryItemSnapshot(string ItemId, int StackSize, int UpgradeLevel = 0, float CraftQuality = 1f);

    /// <summary>One job's full progress (skill/knowledge/fame XP) - id-keyed, so the client
    /// reconciles by JobId rather than assuming list order/count matches.</summary>
    public record JobProgressSnapshot(string JobId, long SkillXp, long KnowledgeXp, long FameXp);

    /// <summary>One rune the player knows, as its base rune id + added word ids -
    /// <c>ResolvedSkill</c> isn't sent (never serialized server-side either); the client
    /// recomputes it locally via RuneManager.Reevaluate after applying.</summary>
    public record RuneSnapshot(string Id, string BaseRuneId, List<string> AddedWordIds);

    /// <summary>
    /// Which item (if any) occupies each equip slot - null slot means empty - with its upgrade
    /// level and craft quality. (These used to be left out on the grounds that equipped items can't
    /// be upgraded - but an item upgraded in the bag and then equipped came back as a plain +0 copy.)
    /// </summary>
    public record EquippedSnapshot(string? WeaponItemId, string? ArmorItemId, string? AccessoryItemId,
        int WeaponUpgradeLevel = 0, float WeaponCraftQuality = 1f,
        int ArmorUpgradeLevel = 0, float ArmorCraftQuality = 1f,
        int AccessoryUpgradeLevel = 0, float AccessoryCraftQuality = 1f);

    /// <summary>
    /// Generic "something about your character changed" push - sent after any session mutation
    /// that isn't already covered by a dedicated result DTO (shop deposit/withdraw, NPC buy/sell,
    /// gather/craft/upgrade, equip/unequip, stat allocation, non-combat heal), so the client's
    /// InventoryGridViewModel/HUD stays correct without hand-rolling a local mirror at every call
    /// site. Combat keeps its own DTOs (CombatTurnResult / GroupCombatSnapshot) since those
    /// already carry precise HP/MP/loot - this fills the gaps everywhere else. Every field is
    /// optional; only sections that actually changed on this action are populated.
    /// </summary>
    public record CharacterUpdateDto(
        List<InventoryItemSnapshot>? InventoryItems,
        long? Gold,
        int? Hp,
        int? MaxHp,
        int? Mp,
        int? MaxMp,
        CharacterProgressResult? Progress,
        List<QuestProgressState>? QuestProgress = null,
        List<JobProgressSnapshot>? Jobs = null,
        List<RuneSnapshot>? Runes = null,
        EquippedSnapshot? Equipment = null);

    public record StartGroupCombatResult(
        bool Success,
        string? Reason,
        List<GroupCombatantState> Characters,
        List<GroupCombatantState> Monsters,
        string CurrentTurnCharacterName);
    public record GroupCombatSnapshot(
        List<GroupCombatantState> Characters,
        List<GroupCombatantState> Monsters,
        string CurrentTurnCharacterName,
        bool Finished,
        bool CharactersWon,
        List<CombatLogMessage> LogEntries);
}
