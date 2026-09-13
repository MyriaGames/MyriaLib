using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Skills;

namespace Myria.Lib.Core.Models
{
    public class SkillData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Class { get; set; }
        public int ManaCost { get; set; }
        public string Type { get; set; }
        public string Target { get; set; }
        public float ScalingFactor { get; set; }
        public string StatToScaleFrom { get; set; }
        public int MinLevel { get; set; }
        public bool IsHealing { get; set; }
        public float AggroModifier { get; set; } = 0f;
        public int Cooldown { get; set; } = 0;
        public List<SkillEffectEntry> Effects { get; set; } = new();
        public List<SkillUpgradeOption> UpgradeOptions { get; set; } = new();
    }

}
