namespace Myria.Lib.Core.Entities.Skills
{
    /// <summary>
    /// A skill's lasting buff (v0.3; the Druid's Verdant Vigor): raises the target's max HP and MP by a
    /// percentage for real-clock minutes - across fights, logouts included - instead of for a few turns
    /// of one fight. The percentages are multiplied by the effective skill's ScalingFactor, so
    /// "power" upgrades (ScalingFactorDelta) make the buff stronger. See <see cref="Characters.LastingBuff"/>.
    /// </summary>
    public class LastingBuffDefinition
    {
        /// <summary>Max HP raise as a fraction (0.05 = +5%) at ScalingFactor 1.</summary>
        public float HpPercent { get; set; }
        /// <summary>Max MP raise as a fraction (0.05 = +5%) at ScalingFactor 1.</summary>
        public float MpPercent { get; set; }
        /// <summary>Real-clock minutes until the buff runs out.</summary>
        public int DurationMinutes { get; set; } = 60;
    }
}
