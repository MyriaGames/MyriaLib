using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Skills;

namespace Myria.Lib.Core.Services
{
    /// <summary>
    /// Per-skill usage-based leveling, replacing the old skill-combination systems. Mirrors
    /// Character.Stats.UnusedPoints/LevelUp/RecalculateUnusedPoints exactly, once per skill instead
    /// of once per character: using a skill grants it usage toward a level; leveling up grants an
    /// automatic baseline power increase (no choice) plus unspent points; points are spent on
    /// skill-specific upgrades (Skill.UpgradeOptions), never combined into a new skill.
    /// </summary>
    public static class SkillLevelingService
    {
        // Usage-count thresholds per skill level. Placeholder curve - exact numbers are balance
        // work (see v0.3.md), not yet tuned. Settable like Character.SkillSlotBreakpoints so a mod
        // or a rebalance pass can replace the curve without a recompile.
        public static (int Uses, int Level)[] LevelBreakpoints { get; set; } =
        {
            (10, 2), (25, 3), (50, 4), (100, 5), (175, 6), (275, 7), (400, 8), (550, 9), (750, 10)
        };

        // Points granted per level gained, and the automatic baseline ScalingFactor growth applied
        // every level (a percentage of the skill's own base ScalingFactor, not a flat amount, so it
        // scales sensibly across weak and strong skills alike) - both placeholders, balance work.
        public const int PointsPerLevel = 1;
        public const float BaselineScalingGrowthPerLevel = 0.05f;

        // Flat gold cost to respec one skill's purchased upgrades - placeholder, balance work.
        public const long RespecCostGold = 100;

        public static SkillProgress GetOrCreate(Character character, string skillId)
        {
            var progress = character.SkillProgress.FirstOrDefault(p => p.SkillId == skillId);
            if (progress == null)
            {
                progress = new SkillProgress { SkillId = skillId };
                character.SkillProgress.Add(progress);
            }
            return progress;
        }

        public static int ResolveLevel(int usageCount)
        {
            int level = 1;
            foreach (var (uses, lvl) in LevelBreakpoints)
                if (usageCount >= uses) level = lvl;
            return level;
        }

        /// <summary>
        /// Anti-tamper recompute: Level and UnspentPoints are always derived fresh from UsageCount
        /// and PurchasedUpgradeIds, never trusted from a client directly - mirrors
        /// Character.RecalculateUnusedPoints. Call after GrantUsage, and on every character load.
        /// </summary>
        public static void RecalculateLevelAndPoints(Character character, string skillId)
        {
            var progress = GetOrCreate(character, skillId);
            progress.Level = ResolveLevel(progress.UsageCount);
            int earned = Math.Max(0, (progress.Level - 1) * PointsPerLevel);
            progress.UnspentPoints = Math.Max(0, earned - progress.PurchasedUpgradeIds.Count);
        }

        /// <summary>Call exactly once per successful cast of this skill in combat (not on mere
        /// lookup/resolution - see ResolveEffectiveSkill, which is side-effect-free).</summary>
        public static void GrantUsage(Character character, string skillId)
        {
            var progress = GetOrCreate(character, skillId);
            progress.UsageCount++;
            RecalculateLevelAndPoints(character, skillId);
        }

        /// <summary>Spends one of this skill's unspent points on one of its own upgrade options.
        /// Returns false (with a reason code in <paramref name="error"/>) if the upgrade doesn't
        /// exist on this skill, is already purchased, or there's no unspent point to spend.</summary>
        public static bool TrySpendPoint(Character character, Skill baseSkill, string upgradeId, out string error)
        {
            RecalculateLevelAndPoints(character, baseSkill.Id); // re-derive before trusting UnspentPoints
            var progress = GetOrCreate(character, baseSkill.Id);

            if (!baseSkill.UpgradeOptions.Any(u => u.Id == upgradeId))
            {
                error = "unknown_upgrade";
                return false;
            }
            if (progress.PurchasedUpgradeIds.Contains(upgradeId))
            {
                error = "already_purchased";
                return false;
            }
            if (progress.UnspentPoints <= 0)
            {
                error = "no_points";
                return false;
            }

            progress.PurchasedUpgradeIds.Add(upgradeId);
            progress.UnspentPoints--;
            error = "";
            return true;
        }

        /// <summary>
        /// Refunds all of this skill's purchased upgrades (never its automatic baseline growth,
        /// which isn't opt-out) so the points can be spent differently. Charging a cost for this is
        /// the caller's responsibility - this only resets the progress state itself.
        /// </summary>
        public static void Respec(Character character, string skillId)
        {
            var progress = GetOrCreate(character, skillId);
            progress.PurchasedUpgradeIds.Clear();
            RecalculateLevelAndPoints(character, skillId);
        }

        /// <summary>
        /// Builds the character-specific effective Skill - the shared template plus this
        /// character's automatic baseline growth and purchased upgrades - without mutating the
        /// template itself (every character of this class holds the same Skill instance from
        /// SkillFactory, see Skill.CloneWithDeltas). Side-effect-free - safe to call from pure
        /// lookups (SkillSlotService.ResolveById, GameHub.ResolveCastableSkill), not just casting.
        /// </summary>
        public static Skill ResolveEffectiveSkill(Character character, Skill baseSkill)
        {
            var progress = character.SkillProgress.FirstOrDefault(p => p.SkillId == baseSkill.Id);
            if (progress == null || (progress.Level <= 1 && progress.PurchasedUpgradeIds.Count == 0))
                return baseSkill; // no progress yet - the raw template is already correct

            float scalingDelta = (progress.Level - 1) * BaselineScalingGrowthPerLevel * baseSkill.ScalingFactor;
            int manaCostDelta = 0, cooldownDelta = 0, castTimeDelta = 0, recoveryTimeDelta = 0;
            var addedEffects = new List<SkillEffectEntry>();

            foreach (var upgradeId in progress.PurchasedUpgradeIds)
            {
                var upgrade = baseSkill.UpgradeOptions.FirstOrDefault(u => u.Id == upgradeId);
                if (upgrade == null) continue; // stale/removed upgrade id (e.g. content changed) - skip, don't throw

                scalingDelta += upgrade.ScalingFactorDelta;
                manaCostDelta += upgrade.ManaCostDelta;
                cooldownDelta += upgrade.CooldownDelta;
                castTimeDelta += upgrade.CastTimeDelta;
                recoveryTimeDelta += upgrade.RecoveryTimeDelta;
                addedEffects.AddRange(upgrade.AddedEffects);
            }

            return baseSkill.CloneWithDeltas(scalingDelta, manaCostDelta, cooldownDelta, castTimeDelta,
                recoveryTimeDelta, addedEffects);
        }
    }
}
