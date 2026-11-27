using Myria.Lib.Core.Entities.Skills;

namespace Myria.Lib.Core.Entities.Characters
{
    /// <summary>
    /// A buff on a character that outlasts fights (v0.3): raises max HP/MP by a percentage until
    /// <see cref="ExpiresUtc"/> on the real clock (it also runs out while logged out). Saved with the
    /// character and only ever created by the server (a skill cast in or out of a fight). One per
    /// skill per character - casting the same skill again replaces it (fresh timer, new strength).
    /// </summary>
    public class LastingBuff
    {
        public string   SkillId    { get; set; } = "";
        public string   Name       { get; set; } = "";
        public string   CasterName { get; set; } = "";
        public float    HpPercent  { get; set; }
        public float    MpPercent  { get; set; }
        public DateTime ExpiresUtc { get; set; }

        /// <summary>The clock buffs are checked against (replaceable in tests).</summary>
        public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        [System.Text.Json.Serialization.JsonIgnore] public bool IsActive => ExpiresUtc > Clock();
        [System.Text.Json.Serialization.JsonIgnore] public TimeSpan Remaining => IsActive ? ExpiresUtc - Clock() : TimeSpan.Zero;

        /// <summary>The buff <paramref name="skill"/> (an effective, upgraded skill) gives when cast by
        /// <paramref name="casterName"/> now; null if the skill has no lasting buff.</summary>
        public static LastingBuff? From(Skill skill, string casterName)
        {
            var def = skill.LastingBuff;
            if (def is null) return null;
            return new LastingBuff
            {
                SkillId    = skill.Id,
                Name       = skill.Name,
                CasterName = casterName,
                HpPercent  = def.HpPercent * skill.ScalingFactor,
                MpPercent  = def.MpPercent * skill.ScalingFactor,
                ExpiresUtc = Clock().AddMinutes(def.DurationMinutes),
            };
        }
    }
}
