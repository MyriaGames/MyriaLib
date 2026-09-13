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

        /// <summary>New effects this upgrade grants on top of the skill's own Effects list (e.g. an
        /// upgrade that adds a stun chance to a skill that didn't have one). To make an *existing*
        /// effect stronger, use ScalingFactorDelta instead - effect magnitude already scales from
        /// the skill's ScalingFactor (see EffectFactory.CreateEffect), so this only needs to cover
        /// genuinely new effects, not strengthening ones the skill already has.</summary>
        public List<SkillEffectEntry> AddedEffects { get; set; } = new();
    }
}
