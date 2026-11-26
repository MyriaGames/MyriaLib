using System.Text.Json;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Jobs;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.NPCs;

namespace Myria.Lib.Core.Services.Manager
{
    public static class JobManager
    {
        private static List<Job> _allJobs = new();

        public static void LoadJobs(string path = "Data/common/jobs.json")
        {
            if (!File.Exists(path)) return;
            var jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path)) ?? new();
            LoadJobs(jobs);
        }

        /// <summary>Loads jobs from already-parsed data (e.g. read from a database).</summary>
        public static void LoadJobs(List<Job> jobs)
        {
            _allJobs = jobs;
        }

        public static Job? GetById(string id)
            => _allJobs.FirstOrDefault(j => j.Id == id);

        public static IReadOnlyList<Job> GetAll() => _allJobs;

        /// <summary>
        /// The NPC whose recipes the Jobs page may craft for <paramref name="jobId"/> - its master
        /// NPC (<see cref="Npc.MasterJobId"/>) that actually has recipes - or null when the job
        /// isn't opted in via <see cref="Job.AllowJobPageCrafting"/> (or has no such NPC).
        /// </summary>
        public static Npc? GetJobPageCraftNpc(string? jobId)
        {
            if (string.IsNullOrEmpty(jobId) || GetById(jobId) is not { AllowJobPageCrafting: true })
                return null;

            return NpcService.AllNpcs.FirstOrDefault(n =>
                string.Equals(n.MasterJobId, jobId, StringComparison.OrdinalIgnoreCase)
                && CraftingService.GetRecipes(n.Id).Length > 0);
        }

        public static TimeSpan JobChangeCooldown         { get; set; } = TimeSpan.FromDays(7);
        public static long     FameTickPerDay            { get; set; } = 5;
        public static long     SkillDecayCap             { get; set; } = 50;
        public static long     FameDecayDivisor          { get; set; } = 200;
        public static double   ActiveJobBonusFraction    { get; set; } = 0.5;

        // ── XP grants ────────────────────────────────────────────────────────────

        /// <summary>
        /// Skill XP — earned by performing the job's main task (crafting/gathering).
        /// Grants a 50 % bonus when this is the player's active job.
        /// Also stamps <see cref="CharacterJob.LastSkillUsedDay"/> so Skill decay (J12) is suppressed today.
        /// </summary>
        public static void GrantSkillXp(Character character, string jobId, long amount)
        {
            if (amount <= 0) return;
            if (character.ActiveJobId == jobId)
                amount += (long)(amount * ActiveJobBonusFraction);
            var entry = GetOrAdd(character, jobId);
            entry.SkillXp += amount;
            entry.LastSkillUsedDay = DayCycleManager.GameDay; // J12: mark skill as used today
        }

        /// <summary>Knowledge XP — earned from job-master NPC quests.</summary>
        public static void GrantKnowledgeXp(Character character, string jobId, long amount)
        {
            if (amount <= 0) return;
            GetOrAdd(character, jobId).KnowledgeXp += amount;
        }

        /// <summary>
        /// Fame XP — earned only when this is the player's active job,
        /// by completing job-related tasks while working it.
        /// </summary>
        public static void GrantFameXp(Character character, string jobId, long amount)
        {
            if (amount <= 0 || character.ActiveJobId != jobId) return;
            GetOrAdd(character, jobId).FameXp += amount;
        }

        // ── Active job ───────────────────────────────────────────────────────────

        /// <summary>
        /// Switches the player's active job.
        /// Clearing the job (null) is always free.
        /// Switching to a new non-null job requires <see cref="JobChangeCooldown"/> since the
        /// last switch; returns false and leaves the job unchanged if the cooldown has not elapsed.
        /// </summary>
        public static bool SetActiveJob(Character character, string? jobId)
        {
            if (jobId == character.ActiveJobId) return true;

            if (jobId != null)
            {
                if (!CanChangeJob(character)) return false;
                character.LastJobChanged = DateTime.UtcNow;
            }

            character.ActiveJobId = jobId;
            return true;
        }

        /// <summary>Returns true when the player is allowed to switch to a new job.</summary>
        public static bool CanChangeJob(Character character)
            => GetCooldownRemaining(character) <= TimeSpan.Zero;

        /// <summary>How long until the player may switch jobs again; zero or negative means ready.</summary>
        public static TimeSpan GetCooldownRemaining(Character character)
            => character.LastJobChanged == DateTime.MinValue
                ? TimeSpan.Zero
                : character.LastJobChanged + JobChangeCooldown - DateTime.UtcNow;

        // ── Selling ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the sell value of <paramref name="item"/> for <paramref name="player"/>,
        /// applying the Fame bonus when the player is actively working the item's job.
        /// Equipment items without an explicit JobId fall back to their UpgradeCategory.
        /// </summary>
        public static int GetSellValue(Item item, Character character)
        {
            int baseSell = item.SellValue;

            string? jobId = item.JobId
                ?? (item is EquipmentItem eq ? eq.UpgradeCategory : null);

            if (jobId == null || jobId != character.ActiveJobId)
                return baseSell;

            var entry = GetOrAdd(character, jobId);
            double multiplier = JobXpService.GetFameMultiplierFromXp(entry.FameXp);
            return (int)(baseSell * multiplier);
        }

        /// <summary>
        /// Returns the amount the <paramref name="seller"/> receives from a player-to-player sale
        /// where the buyer pays <paramref name="agreedPrice"/>.
        /// The seller's active-job Fame level is applied on top of the agreed price;
        /// the buyer is never charged more than <paramref name="agreedPrice"/>.
        /// Returns <paramref name="agreedPrice"/> unchanged when the seller has no active job.
        /// </summary>
        public static int GetCharacterSellReceipt(Character seller, int agreedPrice)
        {
            if (agreedPrice <= 0 || seller.ActiveJobId == null) return agreedPrice;
            var entry = GetOrAdd(seller, seller.ActiveJobId);
            double multiplier = JobXpService.GetFameMultiplierFromXp(entry.FameXp);
            return (int)(agreedPrice * multiplier);
        }

        // ── Gather ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Extra daily gather charges for a player, based on their highest gathering-job knowledge level.
        /// </summary>
        public static int GetGatherKnowledgeBonus(Character character)
        {
            int maxLevel = new[] { "miner", "woodcutter", "herbalist" }
                .Select(id => JobXpService.GetLevel(GetOrAdd(character, id).KnowledgeXp))
                .Max();
            return JobXpService.GetGatherLimitBonus(maxLevel);
        }

        /// <summary>
        /// Items gathered per action for the given job, derived from knowledge level.
        /// Uses the same tier thresholds as the daily gather limit bonus (1 base + 0/1/2/3/4 bonus).
        /// </summary>
        public static int GetGatherAmount(Character character, string jobId)
        {
            int skillLevel = JobXpService.GetLevel(GetOrAdd(character, jobId).SkillXp);
            return 1 + JobXpService.GetGatherLimitBonus(skillLevel);
        }

        // ── Daily ticks (J11–J14) ────────────────────────────────────────────────

        /// <summary>
        /// Applies all per-day job progression changes for the current character.
        /// Call once per game-day (subscribe to <see cref="DayCycleManager.DayAdvanced"/>).
        /// </summary>
        public static void ApplyDailyTicks(Character character, int gameDay)
        {
            foreach (var job in character.Jobs)
            {
                bool isActive = job.JobId == character.ActiveJobId;

                // J11: Passive daily Fame tick — active job only, once per game day
                if (isActive && job.LastFameTickDay < gameDay)
                {
                    job.FameXp += FameTickPerDay;
                    job.LastFameTickDay = gameDay;
                }

                // J12: Skill decay — small loss each day the skill was not used
                if (job.LastSkillUsedDay < gameDay && job.SkillXp > 0)
                {
                    int  level = JobXpService.GetLevel(job.SkillXp);
                    long floor = JobXpService.TotalXpToReach(level);
                    long decay = Math.Min(job.SkillXp - floor, SkillDecayCap);
                    job.SkillXp -= decay;
                }

                // J13: Knowledge decay — progress toward next level resets to 0 daily
                // (levels already earned are preserved; only the partial-level progress drains)
                if (job.KnowledgeXp > 0)
                {
                    int  level = JobXpService.GetLevel(job.KnowledgeXp);
                    job.KnowledgeXp = JobXpService.TotalXpToReach(level);
                }

                // J14: Fame decay — XP (and levels) decay when this is not the active job
                if (!isActive && job.FameXp > 0)
                {
                    long decay = Math.Max(1L, job.FameXp / FameDecayDivisor);
                    job.FameXp = Math.Max(0L, job.FameXp - decay);
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>Returns the player's progress record for a job, creating one if needed.</summary>
        public static CharacterJob GetOrAdd(Character character, string jobId)
        {
            var entry = character.Jobs.FirstOrDefault(j => j.JobId == jobId);
            if (entry != null) return entry;
            entry = new CharacterJob { JobId = jobId };
            character.Jobs.Add(entry);
            return entry;
        }
    }
}
