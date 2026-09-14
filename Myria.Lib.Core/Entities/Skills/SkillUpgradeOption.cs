using Myria.Lib.Core.Entities.Effects;

namespace Myria.Lib.Core.Entities.Skills
{
    /// <summary>
    /// One purchasable upgrade for a specific skill, authored per skill (not a universal menu
    /// shared across all skills) - see Skill.UpgradeOptions. Bought with that skill's own unspent
    /// points (SkillProgress.UnspentPoints), granted from leveling that skill up through use.
    /// Applies as a flat modifier on top of the skill's base fields - see
    /// SkillLevelingService.ResolveEffectiveSkill for how these get combined.
    /// </summary>
    public class SkillUpgradeOption
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";

        public float ScalingFactorDelta { get; set; } = 0f;
        public int ManaCostDelta { get; set; } = 0;      // negative = cheaper
        public int CooldownDelta { get; set; } = 0;      // negative = shorter
        public int CastTimeDelta { get; set; } = 0;
        public int RecoveryTimeDelta { get; set; } = 0;

        /// <summary>Minimum skill Level (SkillProgress.Level) required before this upgrade can be
        /// bought - lets higher-tier upgrades unlock only once a skill has been leveled far enough,
        /// on top of just having a point to spend. Default 1 means available from the first point
        /// earned (a "tier 1" upgrade).</summary>
        public int RequiredLevel { get; set; } = 1;

        /// <summary>How many times this exact upgrade can be purchased, stacking its deltas/effects
        /// again each time - see SkillLevelingService.GetEffectiveMaxPurchases for the actual
        /// resolved cap. 0 (the default) means "infer automatically": unlimited for a purely
        /// numeric upgrade (repeatable power/cost/cooldown tweaks are meant to keep a skill growing
        /// with continued use), but capped to 1 if this upgrade has AddedEffects (granting the same
        /// new effect twice is rarely meaningful - set this explicitly if that's actually wanted).</summary>
        public int MaxPurchases { get; set; } = 0;

        /// <summary>New effects this upgrade grants on top of the skill's own Effects list (e.g. an
        /// upgrade that adds a stun chance to a skill that didn't have one). To make an *existing*
        /// effect stronger, use ScalingFactorDelta instead - effect magnitude already scales from
        /// the skill's ScalingFactor (see EffectFactory.CreateEffect), so this only needs to cover
        /// genuinely new effects, not strengthening ones the skill already has.</summary>
        public List<SkillEffectEntry> AddedEffects { get; set; } = new();
    }
}
