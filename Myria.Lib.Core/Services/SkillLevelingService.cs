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
        // or a rebalance pass can replace the curve without a recompile. Raised from a level-10 cap
        // to level 20 so skills keep leveling well past the point their base-nerf fade (see below)
        // has fully worn off, instead of stalling out at exactly "back to normal".
        public static (int Uses, int Level)[] LevelBreakpoints { get; set; } =
        {
            (10, 2), (25, 3), (50, 4), (100, 5), (175, 6), (275, 7), (400, 8), (550, 9), (750, 10),
            (1000, 11), (1300, 12), (1650, 13), (2050, 14), (2500, 15), (3000, 16), (3550, 17),
            (4150, 18), (4800, 19), (5500, 20)
        };

        // Points granted per level gained, and the automatic per-level ScalingFactor growth applied
        // once a skill has out-leveled its base-nerf fade (levels past NerfFadeReferenceLevel) - a
        // percentage of the skill's own target ScalingFactor, not a flat amount, so it scales
        // sensibly across weak and strong skills alike. Both placeholders, balance work.
        public const int PointsPerLevel = 1;
        public const float BaselineScalingGrowthPerLevel = 0.05f;

        // Flat gold cost to respec one skill's purchased upgrades - placeholder, balance work.
        public const long RespecCostGold = 100;

        // ── Base-nerf fade ──────────────────────────────────────────────────────────────────────
        // A skill's authored numbers in skills.json (ScalingFactor/ManaCost/Cooldown) are treated as
        // its TARGET values - what it plays like once leveled up a bit - not its raw level-1 values.
        // An unleveled skill is deliberately weaker, costlier, and slower to reuse than that target,
        // fading linearly back up to the authored numbers by NerfFadeReferenceLevel, then continuing
        // to grow beyond them for every level past that (via BaselineScalingGrowthPerLevel above).
        // This is what actually makes leveling/upgrading matter - previously the authored numbers
        // were used as-is from level 1, which is what made every skill already feel strong before any
        // real progression. All four constants are placeholders pending balance tuning.
        public const int NerfFadeReferenceLevel = 5;
        public const float BaseScalingNerfFraction = 0.35f;        // e.g. 0.35 = 35% weaker than target at level 1
        public const float BaseManaCostSurchargeFraction = 0.5f;   // e.g. 0.5 = 50% pricier than target at level 1
        public const int BaseCooldownBonusTurns = 2;                // extra cooldown turns added at level 1

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

        /// <summary>Resolves how many times a given upgrade option can be purchased in total - see
        /// SkillUpgradeOption.MaxPurchases for the inference rule. 0 means unlimited (repeatable
        /// indefinitely). Shared by TrySpendPoint (server-authoritative) and any UI that wants to
        /// show purchase state without duplicating the inference rule.</summary>
        public static int GetEffectiveMaxPurchases(SkillUpgradeOption option) =>
            option.MaxPurchases > 0 ? option.MaxPurchases : (option.AddedEffects.Count > 0 ? 1 : 0);

        /// <summary>Spends one of this skill's unspent points on one of its own upgrade options.
        /// Upgrades can be purchased more than once (stacking their deltas again each time) up to
        /// GetEffectiveMaxPurchases, and only once the skill has reached the upgrade's RequiredLevel.
        /// Returns false (with a reason code in <paramref name="error"/>) if the upgrade doesn't
        /// exist on this skill, the skill isn't leveled enough yet, it's already at its purchase
        /// cap, or there's no unspent point to spend.</summary>
        public static bool TrySpendPoint(Character character, Skill baseSkill, string upgradeId, out string error)
        {
            RecalculateLevelAndPoints(character, baseSkill.Id); // re-derive before trusting UnspentPoints
            var progress = GetOrCreate(character, baseSkill.Id);

            var upgrade = baseSkill.UpgradeOptions.FirstOrDefault(u => u.Id == upgradeId);
            if (upgrade == null)
            {
                error = "unknown_upgrade";
                return false;
            }
            if (progress.Level < upgrade.RequiredLevel)
            {
                error = "level_too_low";
                return false;
            }
            int maxPurchases = GetEffectiveMaxPurchases(upgrade);
            if (maxPurchases > 0 && progress.PurchasedUpgradeIds.Count(id => id == upgradeId) >= maxPurchases)
            {
                error = "max_purchases_reached";
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
        /// Builds the character-specific effective Skill - the shared (target-value) template
        /// adjusted for this character's base-nerf fade, automatic post-target growth, and
        /// purchased upgrades - without mutating the template itself (every character of this class
        /// holds the same Skill instance from SkillFactory, see Skill.CloneWithDeltas).
        /// Side-effect-free - safe to call from pure lookups (SkillSlotService.ResolveById,
        /// GameHub.ResolveCastableSkill), not just casting. Always resolves to a clone (even at
        /// level 1 with nothing purchased) since the base-nerf fade means level 1 never equals the
        /// raw authored template unless the nerf constants above are set to zero.
        /// </summary>
        public static Skill ResolveEffectiveSkill(Character character, Skill baseSkill)
        {
            var progress = character.SkillProgress.FirstOrDefault(p => p.SkillId == baseSkill.Id);
            int level = progress?.Level ?? 1;

            // 0 at level 1, 1 once the base-nerf has fully faded at NerfFadeReferenceLevel.
            float fade = NerfFadeReferenceLevel <= 1 ? 1f
                : Math.Clamp((level - 1) / (float)(NerfFadeReferenceLevel - 1), 0f, 1f);

            float targetScaling = baseSkill.ScalingFactor;
            float fadedScaling = level < NerfFadeReferenceLevel
                ? targetScaling * (1f - BaseScalingNerfFraction * (1f - fade))
                : targetScaling * (1f + BaselineScalingGrowthPerLevel * Math.Max(0, level - NerfFadeReferenceLevel));
            float scalingDelta = fadedScaling - targetScaling;

            int targetManaCost = baseSkill.ManaCost;
            int fadedManaCost = level < NerfFadeReferenceLevel
                ? (int)Math.Round(targetManaCost * (1f + BaseManaCostSurchargeFraction * (1f - fade)))
                : targetManaCost;
            int manaCostDelta = fadedManaCost - targetManaCost;

            int cooldownDelta = level < NerfFadeReferenceLevel
                ? (int)Math.Round(BaseCooldownBonusTurns * (1f - fade))
                : 0;

            int castTimeDelta = 0, recoveryTimeDelta = 0;
            var addedEffects = new List<SkillEffectEntry>();

            if (progress != null)
            {
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
            }

            if (scalingDelta == 0f && manaCostDelta == 0 && cooldownDelta == 0 && castTimeDelta == 0
                && recoveryTimeDelta == 0 && addedEffects.Count == 0)
                return baseSkill; // nerf constants disabled and nothing purchased - raw template is exact

            return baseSkill.CloneWithDeltas(scalingDelta, manaCostDelta, cooldownDelta, castTimeDelta,
                recoveryTimeDelta, addedEffects);
        }
    }
}
