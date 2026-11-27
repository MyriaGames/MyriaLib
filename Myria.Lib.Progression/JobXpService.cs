namespace Myria.Lib.Core.Services
{
    /// <summary>
    /// XP and level calculations for job aspects (Skill, Knowledge, Fame).
    /// Levelling cost: XpForLevel(n) = n × XpCostBase (default linear, max level 100).
    /// Cumulative XP to reach level L = XpCostBase × (L-1) × L / 2.
    /// </summary>
    public static class JobXpService
    {
        public static int    MaxLevel      { get; set; } = 100;
        public static long   XpCostBase    { get; set; } = 1_000;

        /// <summary>
        /// Added to 1.0 to produce the max Fame sell multiplier.
        /// Default 1.5 → multiplier scales from ×1.0 to ×2.5.
        /// </summary>
        public static double MaxFameBonus  { get; set; } = 1.5;

        /// <summary>
        /// Added to 1.0 to produce the max Skill gather/craft multiplier.
        /// Default 2.0 → multiplier scales from ×1.0 to ×3.0.
        /// </summary>
        public static double MaxSkillBonus { get; set; } = 2.0;

        /// <summary>
        /// Knowledge-level thresholds granting extra daily gather charges.
        /// Must be sorted ascending by Level. Default: (10,+1),(30,+2),(60,+3),(100,+4).
        /// </summary>
        public static (int Level, int Bonus)[] GatherBonusThresholds { get; set; } =
        {
            (10, 1), (30, 2), (60, 3), (100, 4)
        };

        /// <summary>
        /// Baseline max upgrade level when no gate threshold is met.
        /// Default: 2.
        /// </summary>
        public static int DefaultMaxUpgrade { get; set; } = 2;

        /// <summary>
        /// Knowledge-level gates controlling maximum equipment upgrade levels.
        /// Must be sorted ascending by Level. Default: (10,4),(30,6),(60,8),(100,10).
        /// </summary>
        public static (int Level, int MaxUpgrade)[] UpgradeGates { get; set; } =
        {
            (10, 4), (30, 6), (60, 8), (100, 10)
        };

        // ── XP calculations ───────────────────────────────────────────────────────

        /// <summary>XP required to advance from level <paramref name="level"/> to the next.</summary>
        public static long XpForLevel(int level)
            => level >= MaxLevel ? long.MaxValue : level * XpCostBase;

        /// <summary>Cumulative XP required to reach exactly level <paramref name="level"/>.</summary>
        public static long TotalXpToReach(int level)
        {
            if (level <= 1) return 0;
            long n = level - 1;
            return XpCostBase * n * (n + 1) / 2;
        }

        /// <summary>Current level (1–MaxLevel) derived from total accumulated XP.</summary>
        public static int GetLevel(long totalXp)
        {
            int level = 1;
            while (level < MaxLevel && totalXp >= TotalXpToReach(level + 1))
                level++;
            return level;
        }

        /// <summary>XP earned within the current level.</summary>
        public static long XpInCurrentLevel(long totalXp)
            => totalXp - TotalXpToReach(GetLevel(totalXp));

        /// <summary>XP required to complete the current level.</summary>
        public static long XpForCurrentLevel(long totalXp)
        {
            int level = GetLevel(totalXp);
            return level >= MaxLevel ? 1 : XpForLevel(level);
        }

        /// <summary>Progress fraction (0.0–1.0) within the current level.</summary>
        public static double GetProgressFraction(long totalXp)
        {
            if (GetLevel(totalXp) >= MaxLevel) return 1.0;
            return (double)XpInCurrentLevel(totalXp) / XpForCurrentLevel(totalXp);
        }

        /// <summary>"1,200 / 3,400 XP" progress string, or "MAX" at max level.</summary>
        public static string FormatProgress(long totalXp)
        {
            if (GetLevel(totalXp) >= MaxLevel) return "MAX";
            return $"{XpInCurrentLevel(totalXp):N0} / {XpForCurrentLevel(totalXp):N0} XP";
        }

        // ── Multipliers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sell-value multiplier from Fame level.
        /// Scales linearly from ×1.0 (level 1) to ×(1.0 + MaxFameBonus) (max level).
        /// </summary>
        public static double GetFameMultiplier(int level)
        {
            int clamped = Math.Clamp(level, 1, MaxLevel);
            return 1.0 + (clamped - 1) * (MaxFameBonus / (MaxLevel - 1));
        }

        /// <summary>Fame sell multiplier derived from accumulated Fame XP.</summary>
        public static double GetFameMultiplierFromXp(long fameXp)
            => GetFameMultiplier(GetLevel(fameXp));

        /// <summary>
        /// Multiplier applied to gathered amounts or crafted item stats.
        /// Scales linearly from ×1.0 (level 1) to ×(1.0 + MaxSkillBonus) (max level).
        /// </summary>
        public static double GetSkillMultiplier(int level)
        {
            int clamped = Math.Clamp(level, 1, MaxLevel);
            return 1.0 + (clamped - 1) * (MaxSkillBonus / (MaxLevel - 1));
        }

        /// <summary>
        /// Quality of crafted (or master-upgraded) equipment from the crafter's skill level (v0.3):
        /// x1.0 at level 1 up to x(1 + MaxSkillBonus) = x3.0 at max level, on a convex curve - about
        /// x1.05 at 10, x1.3 at 30, x1.7 at 50, x2.4 at 80 - so the best gear comes from a master
        /// smith and a low-level smith adds little. (It used the linear gather multiplier.)
        /// </summary>
        public static double GetCraftQuality(int level)
        {
            int clamped = Math.Clamp(level, 1, MaxLevel);
            double t = (clamped - 1) / (double)(MaxLevel - 1);
            return 1.0 + MaxSkillBonus * Math.Pow(t, CraftQualityExponent);
        }

        /// <summary>Shape of <see cref="GetCraftQuality"/>: 1 = linear, higher = slower early growth.</summary>
        public static double CraftQualityExponent { get; set; } = 1.5;

        public static double GetCraftQualityFromXp(long totalXp) => GetCraftQuality(GetLevel(totalXp));

        /// <summary>Skill multiplier derived from accumulated XP rather than a precomputed level.</summary>
        public static double GetSkillMultiplierFromXp(long totalXp)
            => GetSkillMultiplier(GetLevel(totalXp));

        /// <summary>
        /// Converts the skill multiplier into a concrete gathered integer amount using
        /// probabilistic rounding so progression feels smooth at every level.
        /// E.g. multiplier 1.7 → 70% chance of 2, 30% chance of 1.
        /// </summary>
        public static int ApplyGatherMultiplier(long skillXp)
        {
            double mult  = GetSkillMultiplierFromXp(skillXp);
            int    floor = (int)mult;
            return Random.Shared.NextDouble() < (mult - floor) ? floor + 1 : floor;
        }

        // ── Threshold lookups ─────────────────────────────────────────────────────

        /// <summary>
        /// Extra daily gather charges from knowledge level.
        /// Determined by <see cref="GatherBonusThresholds"/>.
        /// </summary>
        public static int GetGatherLimitBonus(int knowledgeLevel)
        {
            int bonus = 0;
            foreach (var (level, b) in GatherBonusThresholds)
                if (knowledgeLevel >= level) bonus = b;
            return bonus;
        }

        /// <summary>
        /// Maximum equipment upgrade level unlocked by the given knowledge level.
        /// Determined by <see cref="UpgradeGates"/> and <see cref="DefaultMaxUpgrade"/>.
        /// </summary>
        public static int GetMaxUpgradeLevel(int knowledgeLevel)
        {
            int max = DefaultMaxUpgrade;
            foreach (var (level, upgrade) in UpgradeGates)
                if (knowledgeLevel >= level) max = upgrade;
            return max;
        }
    }
}
