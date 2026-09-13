using System.Text.Json.Serialization;

namespace Myria.Lib.Core.Entities.Skills
{
    public enum SlottedSkillSource { Regular }

    /// <summary>
    /// One entry in the player's combat skill bar.
    /// Serialized as part of the player save. <see cref="ResolvedSkill"/> is populated
    /// at runtime by <c>SkillSlotService.ResolveSlots</c>.
    /// </summary>
    public class SkillSlot
    {
        /// <summary>Which pool the skill comes from.</summary>
        public SlottedSkillSource Source { get; set; }

        /// <summary>The <c>Skill.Id</c> from <c>player.Skills</c> this slot holds.</summary>
        public string SkillId { get; set; } = "";

        /// <summary>The resolved combat skill. Populated at runtime — not serialized.</summary>
        [JsonIgnore]
        public Skill? ResolvedSkill { get; set; }
    }
}
