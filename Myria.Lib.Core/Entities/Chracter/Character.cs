using System.Text.Json.Serialization;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Jobs;
using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Models.BaseModel;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Events;
using Myria.Lib.Core.Utils;
namespace Myria.Lib.Core.Entities.Characters
{
    public class Character : CombatEntity
    {
        public event EventHandler<SkillLearnedEventArgs>? SkillLearned;
        public event EventHandler<XpGainedEventArgs>? XpGained;
        public event EventHandler<LevelUpEventArgs>? LeveledUp;
        public event EventHandler<HealthChangedEventArgs>? HealthChanged;
        public event EventHandler<ManaChangedEventArgs>? ManaChanged;
        [JsonConverter(typeof(CharacterClassJsonConverter))]
        public string Class { get; set; } = CharacterClass.Fighter;
        [JsonConverter(typeof(CharacterRaceJsonConverter))]
        public string Race  { get; set; } = CharacterRace.Myralu;
        public int Level { get; set; } = 1;
        public long Experience { get; set; } = 0;
        public long ExpForNextLvl { get; set; }
        public int PotionTierAvailable { get; set; } = 1;
        public Inventory Inventory { get; set; } = new();
        public MoneyBag Money { get; set; } = new();
        public List<Skill> Skills { get; set; } = new();
        public List<Quest> ActiveQuests { get; set; } = new();
        public List<Quest> CompletedQuests { get; set; } = new();
        public Dictionary<string, RepeatRecord> RepeatableQuestRecords { get; set; } = new();
        public Dictionary<string, string> ModState { get; set; } = new();
        [JsonIgnore]
        public Room CurrentRoom { get; set; }
        public int CurrentRoomId { get; set; }

        // Runtime-only aggro level, reset at the start of each GroupCombatEncounter.
        // Determines which character monsters will focus on.
        [JsonIgnore]
        public float AggroLevel { get; set; }
        public int? LastHealerRoomId { get; set; } = null;
        public Dictionary<int, DateTime> RoomGatheringStatus { get; set; } = new();

        /// <summary>
        /// Level breakpoints for <see cref="SkillSlotCount"/>. Base is 1 slot; each entry raises
        /// the cap once Level reaches it. Pass entries sorted ascending by level — mirrors the
        /// shape and the same ordering assumption as JobXpService.GatherBonusThresholds/
        /// UpgradeGates. A mod or a differently-paced game can replace this instead of being stuck
        /// with a hardcoded curve.
        /// </summary>
        public static (int Level, int Slots)[] SkillSlotBreakpoints { get; set; } =
        {
            (3, 2), (9, 3), (18, 4), (27, 5), (36, 6), (45, 7), (54, 8), (63, 9), (72, 10)
        };

        private static int ResolveSlotCount(int level)
        {
            int slots = 1;
            foreach (var (breakLevel, breakSlots) in SkillSlotBreakpoints)
                if (level >= breakLevel) slots = breakSlots;
            return slots;
        }

        // ── Combat Skill Slots ────────────────────────────────────────────────────
        /// <summary>
        /// The ordered list of skills configured for the combat skill bar.
        /// Only slotted skills appear during a fight. Capped by <see cref="SkillSlotCount"/>.
        /// </summary>
        public List<SkillSlot> SkillSlots { get; set; } = new();

        /// <summary>
        /// Maximum number of skills the player can slot for combat, based on level.
        /// See <see cref="SkillSlotBreakpoints"/> for the level curve.
        /// </summary>
        [JsonIgnore]
        public int SkillSlotCount => ResolveSlotCount(Level);

        /// <summary>Per-skill leveling progress (usage count, level, unspent points, purchased
        /// upgrades) - keyed by skill id via SkillLevelingService.GetOrCreate. A skill with no
        /// entry here simply hasn't been used yet (level 1, no points).</summary>
        public List<SkillProgress> SkillProgress { get; set; } = new();

        // ── Race ─────────────────────────────────────────────────────────────────
        /// <summary>
        /// False for characters created before the race-selection UI existed.
        /// The loading path uses this to trigger a one-time migration.
        /// </summary>
        public bool RaceSelected { get; set; } = false;

        // ── Class XP ─────────────────────────────────────────────────────────────
        public Dictionary<string, long> ClassXp { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime LastClassPenaltyApplied { get; set; } = DateTime.MinValue;
        /// <summary>UTC timestamp of the last class switch; DateTime.MinValue = never switched (no cooldown).</summary>
        public DateTime LastClassChanged { get; set; } = DateTime.MinValue;

        protected override int ExtraSTR => ClassManager.GetClassBonusForStat(this, "STR");
        protected override int ExtraDEX => ClassManager.GetClassBonusForStat(this, "DEX");
        protected override int ExtraEND => ClassManager.GetClassBonusForStat(this, "END");
        protected override int ExtraINT => ClassManager.GetClassBonusForStat(this, "INT");
        protected override int ExtraSPR => ClassManager.GetClassBonusForStat(this, "SPR");
        protected override int ExtraBaseHealth => ClassManager.GetClassHpBonus(this);
        protected override int ExtraBaseMana   => ClassManager.GetClassManaBonus(this);

        // ── Jobs ─────────────────────────────────────────────────────────────────
        public string? ActiveJobId { get; set; }
        public List<CharacterJob> Jobs { get; set; } = new();
        /// <summary>UTC timestamp of the last time the player switched to a new active job.</summary>
        public DateTime LastJobChanged { get; set; } = DateTime.MinValue;

        // ── Runic Magic (WPF / Unity — magic classes) ────────────────────────────
        /// <summary>All runes the player knows, including those gained via word combinations.</summary>
        public List<CompositeRune> KnownRunes { get; set; } = new();

        /// <summary>The player's runic word discovery and translation state.</summary>
        public List<CharacterRuneWordEntry> RuneDictionary { get; set; } = new();

        // Add inventory, experience, commands, etc.
        public Character(string name, Stats stats)
        {
            Name = name;
            Stats = stats;
            CurrentHealth = MaxHealth;
            CurrentMana = MaxMana;
            ExpForNextLvl = (long)(Math.Pow(Level, 2)) * 50;
        }
        public void GainXp(long amount)
        {
            if (amount <= 0) return;

            Experience += amount;

            // Level-up loop in case you gain a lot at once
            while (Experience >= ExpForNextLvl)
            {
                int old = Level;
                Experience -= ExpForNextLvl;
                LevelUp(); // your existing method (or implement it)
                LeveledUp?.Invoke(this, new LevelUpEventArgs(old, Level));
            }

            XpGained?.Invoke(this, new XpGainedEventArgs(
                amount,
                Experience,
                ExpForNextLvl
            ));

        }
        public override void TakeDamage(int amount)
        {
            int old = CurrentHealth;
            base.TakeDamage(amount);
            if (CurrentHealth != old)
                HealthChanged?.Invoke(this, new HealthChangedEventArgs(old, CurrentHealth, null));
        }

        public int ApplyDamage(int amount, string? source = null)
        {
            if (amount <= 0) return 0;

            int old = CurrentHealth;
            int newValue = Math.Max(0, CurrentHealth - amount);
            CurrentHealth = newValue;

            int actual = old - newValue;
            if (actual != 0)
                HealthChanged?.Invoke(this, new HealthChangedEventArgs(old, newValue, source));

            return actual;
        }

        public override int Heal(int amount, string? source = null)
        {
            if (amount <= 0) return 0;

            int old = CurrentHealth;
            int max = MaxHealth;
            if (amount > max)
                amount = max;
            int newValue = Math.Min(max, CurrentHealth + amount);
            CurrentHealth = newValue;

            int actual = newValue - old;
            if (actual != 0)
                HealthChanged?.Invoke(this, new HealthChangedEventArgs(old, newValue, source));

            return actual;
        }

        /// <summary>
        /// Sets health to an absolute value (e.g. server-authoritative multiplayer combat
        /// results) and fires <see cref="HealthChanged"/> if it actually changed — unlike
        /// assigning <see cref="CurrentHealth"/> directly, which UI listeners never see.
        /// </summary>
        public void SetHealth(int newValue, string? source = null)
        {
            int old = CurrentHealth;
            newValue = Math.Clamp(newValue, 0, MaxHealth);
            if (newValue == old) return;
            CurrentHealth = newValue;
            HealthChanged?.Invoke(this, new HealthChangedEventArgs(old, newValue, source));
        }

        /// <summary>Mana counterpart to <see cref="SetHealth"/>.</summary>
        public void SetMana(int newValue, string? source = null)
        {
            int old = CurrentMana;
            newValue = Math.Clamp(newValue, 0, MaxMana);
            if (newValue == old) return;
            CurrentMana = newValue;
            ManaChanged?.Invoke(this, new ManaChangedEventArgs(old, newValue, source));
        }

        /// <summary>
        /// Overwrites this character's level-derived state (Level/Experience/ExpForNextLvl,
        /// base stats, unused stat points, base health/mana) with the server's authoritative
        /// values, fired after any action that might have granted XP - replacing the old
        /// approach of the client replaying GainXp()/LevelUp() locally, which drifts whenever
        /// a rookie bonus, level-gap XP scaling, or anything else server-side doesn't match
        /// the client's own math exactly. Fires LeveledUp if Level actually changed, same as
        /// SetHealth/SetMana do for their own events.
        /// </summary>
        public void ApplySyncedProgress(CharacterProgressResult p)
        {
            int  oldLevel      = Level;
            long oldExperience = Experience;

            Level         = p.Level;
            Experience    = p.Experience;
            ExpForNextLvl = p.ExpForNextLvl;

            Stats.Strength     = p.Strength;
            Stats.Dexterity    = p.Dexterity;
            Stats.Endurance    = p.Endurance;
            Stats.Intelligence = p.Intelligence;
            Stats.Spirit       = p.Spirit;
            Stats.UnusedPoints = p.UnusedPoints;
            Stats.BaseHealth   = p.BaseHealth;
            Stats.BaseMana     = p.BaseMana;

            if (Level != oldLevel)
            {
                // LeveledUp alone is enough to refresh every UI listener (incl. the HUD's XP
                // bar) for this case - see XpGained's own doc note below for why it isn't also
                // fired here.
                LeveledUp?.Invoke(this, new LevelUpEventArgs(oldLevel, Level));
            }
            else if (Experience != oldExperience)
            {
                // No level boundary crossed, so the exact amount gained is just the delta -
                // fire XpGained so listeners that only watch HealthChanged/ManaChanged/
                // LeveledUp (e.g. CharacterHeaderVm's HUD XP bar) actually hear about it too.
                // A multiplayer combat win otherwise updated Experience with nothing raising
                // any event the HUD listens to, so the bar visibly lagged behind the real value
                // until an unrelated HealthChanged/ManaChanged happened to fire later (e.g. at
                // the Healer). Deliberately skipped on a level-up above: this method only gets
                // the resulting Level/Experience snapshot, not the raw amount granted, so an
                // exact delta can't be reconstructed once a level boundary (and its own,
                // different ExpForNextLvl) was crossed - GainXp (the local/offline path) still
                // reports an exact amount in that case because it receives the raw grant itself.
                XpGained?.Invoke(this, new XpGainedEventArgs(Experience - oldExperience, Experience, ExpForNextLvl));
            }
        }

        /// <summary>
        /// Reconciles <see cref="Jobs"/> against the server's authoritative per-job XP - id-keyed
        /// (updates an existing entry in place, adds one if the job is new to this character),
        /// so job/skill XP granted server-side (Gather/Craft/Upgrade) actually reaches the client
        /// instead of only ever existing in the client's own local GrantSkillXp replay.
        /// </summary>
        public void ApplySyncedJobs(IEnumerable<JobProgressSnapshot> jobs)
        {
            foreach (var snap in jobs)
            {
                var job = Jobs.FirstOrDefault(j => j.JobId == snap.JobId);
                if (job is null)
                {
                    job = new CharacterJob { JobId = snap.JobId };
                    Jobs.Add(job);
                }
                job.SkillXp     = snap.SkillXp;
                job.KnowledgeXp = snap.KnowledgeXp;
                job.FameXp      = snap.FameXp;
            }
        }

        /// <summary>
        /// Overwrites <see cref="KnownRunes"/> with the server's authoritative rune collection -
        /// whole-list replace (runes can be granted/transformed/lost in ways more complex than a
        /// per-id XP bump), recomputing each <see cref="CompositeRune.ResolvedSkill"/> locally
        /// afterward since that field is never sent over the wire (it isn't serialized
        /// server-side either - see CompositeRune's own remarks).
        /// </summary>
        public void ApplySyncedRunes(IEnumerable<RuneSnapshot> runes)
        {
            KnownRunes = runes.Select(r =>
            {
                var rune = new CompositeRune
                {
                    Id = r.Id,
                    BaseRuneId = r.BaseRuneId,
                    AddedWordIds = new List<string>(r.AddedWordIds)
                };
                Myria.Lib.Core.Services.Manager.RuneManager.Reevaluate(rune);
                return rune;
            }).ToList();
        }

        /// <summary>
        /// Applies a generic server-authoritative <see cref="CharacterUpdateDto"/> push - each
        /// section is optional and only touched if present, routed to the same
        /// SetHealth/SetMana/ApplySyncedProgress/Inventory.ApplySnapshot methods a dedicated
        /// sync path would use, so every existing UI listener (HealthChanged, ManaChanged,
        /// LeveledUp, ItemReceived/ItemRemoved) reacts exactly as if the change happened locally.
        /// </summary>
        public void ApplyCharacterUpdate(CharacterUpdateDto update, string? source = null)
        {
            if (update.InventoryItems is not null)
                Inventory.ApplySnapshot(update.InventoryItems, this);

            if (update.Gold is long gold)
                Money.SetBalance(gold);

            // MaxHp/MaxMp on the DTO are informational only (what the server computed at send
            // time) - MaxHealth/MaxMana are derived properties here (Stats + gear), so there's
            // nothing to "set"; they'll already be correct once Progress (below) is applied.
            if (update.Hp is int hp)
                SetHealth(hp, source);
            if (update.Mp is int mp)
                SetMana(mp, source);

            if (update.Progress is not null)
                ApplySyncedProgress(update.Progress);

            if (update.QuestProgress is not null)
                ApplySyncedQuestProgress(update.QuestProgress);

            if (update.Jobs is not null)
                ApplySyncedJobs(update.Jobs);

            if (update.Runes is not null)
                ApplySyncedRunes(update.Runes);

            if (update.Equipment is not null)
                ApplySyncedEquipment(update.Equipment);

            if (update.SkillProgress is not null)
                ApplySyncedSkillProgress(update.SkillProgress);
        }

        /// <summary>Overwrites SkillProgress with the server's authoritative per-skill leveling
        /// state - whole-list replace, matching ApplySyncedRunes/ApplySyncedJobs's shape.</summary>
        public void ApplySyncedSkillProgress(IEnumerable<SkillProgressSnapshot> progress)
        {
            SkillProgress = progress.Select(p => new SkillProgress
            {
                SkillId = p.SkillId,
                UsageCount = p.UsageCount,
                Level = p.Level,
                UnspentPoints = p.UnspentPoints,
                PurchasedUpgradeIds = new List<string>(p.PurchasedUpgradeIds)
            }).ToList();
        }

        /// <summary>
        /// Overwrites the three equip slots with the server's authoritative item ids (null id =
        /// empty slot) - reconciles cases where the server's SwapEquipment/unequip result differs
        /// from what this client's own optimistic mirror already applied (e.g. a rejected swap),
        /// which would otherwise permanently desync gear-derived stats (MaxHealth/MaxMana/attack/
        /// defense) from what server-authoritative combat actually uses. Recreates each slot's
        /// item fresh by id via ItemFactory, same identity-only tradeoff as inventory snapshots -
        /// see EquippedSnapshot's own remarks on why that's a non-issue for equipped gear.
        /// </summary>
        public void ApplySyncedEquipment(EquippedSnapshot equipment)
        {
            WeaponSlot    = ResolveEquippedSlot(equipment.WeaponItemId);
            ArmorSlot     = ResolveEquippedSlot(equipment.ArmorItemId);
            AccessorySlot = ResolveEquippedSlot(equipment.AccessoryItemId);

            static EquipmentItem? ResolveEquippedSlot(string? itemId) =>
                itemId is not null && ItemFactory.TryCreateItem(itemId, out var item) ? item as EquipmentItem : null;
        }

        /// <summary>
        /// Overwrites kill/item objective progress on matching active quests with the server's
        /// authoritative counters. Quests are matched by Id; anything the client doesn't
        /// currently have active (e.g. already turned in) is ignored.
        /// </summary>
        public void ApplySyncedQuestProgress(IEnumerable<QuestProgressState> progress)
        {
            foreach (var p in progress)
            {
                var quest = ActiveQuests.FirstOrDefault(q => q.Id == p.QuestId);
                if (quest is null) continue;
                quest.KillProgress = new Dictionary<int, int>(p.KillProgress);
                quest.ItemProgress = new Dictionary<string, int>(p.ItemProgress);
            }
        }

        public int SpendMana(int amount, string? source = null)
        {
            if (amount <= 0) return 0;

            int old = CurrentMana;
            int newValue = Math.Max(0, CurrentMana - amount);
            CurrentMana = newValue;

            int actual = old - newValue;
            if (actual != 0)
                ManaChanged?.Invoke(this, new ManaChangedEventArgs(old, newValue, source));

            return actual;
        }

        public override int RestoreMana(int amount, string? source = null)
        {
            if (amount <= 0) return 0;
            int old = CurrentMana;
            int max = MaxMana;
            if (amount > max)
                amount = max;
            int newValue = Math.Min(max, CurrentMana + amount);
            CurrentMana = newValue;

            int actual = newValue - old;
            if (actual != 0)
                ManaChanged?.Invoke(this, new ManaChangedEventArgs(old, newValue, source));

            return actual;
        }
        /// <summary>
        /// Applies an death penalty if the player dies
        /// </summary>
        public void ApplyDeathXpPenalty()
        {
            long penalty = (long)(ExpForNextLvl * 0.01f);
            long actualLoss = Math.Min(penalty, Experience);
            Experience -= actualLoss;
        }
        /// <summary>
        /// equips an equipment item to its slot
        /// </summary>
        /// <param name="item">item to equip</param>
        public void Equip(EquipmentItem item)
        {
            if (Equipped.GetValueOrDefault(item.SlotType) is { } previous)
                Inventory.AddItem(previous, this);
            Equipped[item.SlotType] = item;
        }
        /// <summary>
        /// updates stats for an Level up
        /// </summary>
        public void LevelUp()
        {
            var profile = RaceProfile.All[Race];

            Level++;
            foreach (var (statId, growth) in profile.StatGrowth)
                Stats.SetBase(statId, Stats.GetBase(statId) + growth);
            Stats.UnusedPoints++;

            Stats.BaseHealth += profile.HpPerLevel;
            Stats.BaseMana += profile.ManaPerLevel;

            CurrentHealth = MaxHealth;
            CurrentMana = MaxMana;
            ExpForNextLvl = (long)(Math.Pow(Level, 2)) * 50;
        }
        public bool LearnSkill(Skill skill)
        {
            if (skill == null) return false;

            // prevent duplicates (use Id if you have it)
            if (Skills.Any(s => s.Id == skill.Id))
                return false;

            Skills.Add(skill);

            SkillLearned?.Invoke(this, new SkillLearnedEventArgs(skill));
            return true;
        }

        /// <summary>
        /// Returns true if the player has a tool that enables the given gathering type —
        /// checks both the inventory bag and the equipped weapon slot.
        /// </summary>
        public bool HasToolFor(string type)
        {
            if (type == GatheringType.Herb) return true;
            return Inventory.Items.Any(i => i.ToolType == type)
                || WeaponSlot?.ToolType == type;
        }

        /// <summary>
        /// Reverts any quest that is marked Completed but whose requirements are not actually met.
        /// Corrects saves that were written while the completion bug was active.
        /// </summary>
        public void ValidateQuestStatuses()
        {
            foreach (var quest in ActiveQuests.Where(q => q.Status == QuestStatus.Completed))
            {
                bool killsDone = quest.RequiredKills.All(rk =>
                    quest.KillProgress.TryGetValue(rk.Key, out int kills) && kills >= rk.Value);
                bool itemsDone = quest.RequiredItems.All(ri =>
                    quest.ItemProgress.TryGetValue(ri.Key, out int items) && items >= ri.Value);

                if (!killsDone || !itemsDone)
                    quest.Status = QuestStatus.InProgress;
            }
        }

        /// <summary>
        /// Recalculates unused stat points based on level and spent points.
        /// Useful for imported characters or save migrations.
        /// </summary>
        public void RecalculateUnusedPoints()
        {
            // 1 point per level starting from level 2 (level 1 has 0 points)
            int totalPointsEarned = Math.Max(0, Level - 1);

            int pointsSpent = Stats.BonusValues.Values.Sum();

            Stats.UnusedPoints = Math.Max(0, totalPointsEarned - pointsSpent);
        }

    }

}
