namespace Myria.Lib.Core.Entities.Skills
{
    /// <summary>
    /// One character's leveling progress on one specific skill. Keyed by SkillId on
    /// Character.SkillProgress - a skill a character hasn't used yet simply has no entry (treated
    /// as level 1, 0 usage, 0 unspent points).
    /// <para>
    /// UsageCount is the only value ever written directly (by SkillLevelingService.GrantUsage,
    /// once per successful cast) - Level and UnspentPoints are always derived from it plus
    /// PurchasedUpgradeIds via SkillLevelingService.RecalculateLevelAndPoints, the same
    /// earned-minus-spent anti-tamper pattern Character.RecalculateUnusedPoints already uses for
    /// stat points. Never trust a client-reported Level/UnspentPoints directly.
    /// </para>
    /// </summary>
    public class SkillProgress
    {
        public string SkillId { get; set; } = "";
        public int UsageCount { get; set; } = 0;
        public int Level { get; set; } = 1;
        public int UnspentPoints { get; set; } = 0;
        public List<string> PurchasedUpgradeIds { get; set; } = new();
    }
}
