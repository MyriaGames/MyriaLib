using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Skills;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Skill.Range (added for Myria.Mono's ranged-class attack reach — see WorldScreen.
/// ResolveSkillRange) defaults to 0, meaning "inherit the caster's class-level reach" rather than
/// a literal zero range. SkillLevelingService.LevelUp builds each character's effective skill via
/// CloneWithDeltas, which is a manual field-by-field copy — a new field left out of it silently
/// reverts to 0 on every level-up, which for Range specifically wouldn't even look broken (it'd
/// just quietly fall back to the class default), so this needs its own explicit assertion rather
/// than relying on some other symptom to catch a missed field.
/// </summary>
public class SkillRangeTests
{
    private static Skill MakeSkill(float range) => new()
    {
        Id = "test_skill",
        Name = "Test Skill",
        Description = "",
        ManaCost = 5,
        Type = SkillType.Physical,
        Target = "SingleEnemy",
        ScalingFactor = 1f,
        StatToScaleFrom = "ATK",
        Range = range,
    };

    [Fact]
    public void DefaultsToZeroMeaningInheritTheClassRange()
    {
        var skill = new Skill
        {
            Id = "x", Name = "x", Description = "", Target = "SingleEnemy", StatToScaleFrom = "ATK",
        };
        Assert.Equal(0f, skill.Range);
    }

    [Fact]
    public void CloneWithDeltasPreservesRange()
    {
        var skill = MakeSkill(9f);

        var clone = skill.CloneWithDeltas(
            scalingFactorDelta: 0.5f, manaCostDelta: 1, cooldownDelta: 0,
            castTimeDelta: 0, recoveryTimeDelta: 0, addedEffects: Array.Empty<SkillEffectEntry>());

        Assert.Equal(9f, clone.Range);
    }
}
