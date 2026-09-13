using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Interfaces;
using System.Text.Json.Serialization;

namespace Myria.Lib.Core.Entities.Skills
{
    public enum SkillType { Physical, Magical }

    public class Skill
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int CastTime { get; set; } = 0;     // turns before it activates
        public int RecoveryTime { get; set; } = 0; // turns before you can act again (universal, any skill)
        // Turns before THIS specific skill can be used again, independent of RecoveryTime - 0 means
        // no cooldown (usable every time RecoveryTime allows an action, same as before this existed).
        public int Cooldown { get; set; } = 0;
        [JsonConverter(typeof(CharacterClassJsonConverter))]
        public string Class { get; set; } = "";
        public bool IsHealing { get; set; } = false;
        public int ManaCost { get; set; }
        public SkillType Type { get; set; }
        [JsonConverter(typeof(SkillTargetJsonConverter))]
        public string Target { get; set; }

        public float ScalingFactor { get; set; }
        public string StatToScaleFrom { get; set; } = "ATK";

        public int MinLevel { get; set; } = 1;

        // Extra aggro generated when this skill is cast, on top of the base +1.
        // Default 0 means the skill generates the standard 1.0 aggro.
        public float AggroModifier { get; set; } = 0f;

        // Data-driven effects applied when the skill fires.
        // Each entry references an EffectDefinition by ID and specifies who receives it.
        public List<SkillEffectEntry> Effects { get; set; } = new();

        // Optional code-defined effect hook for special cases not covered by EffectDefinition.
        [JsonIgnore]
        public Action<Character, ICombatant>? Effect { get; set; }
    }

}
