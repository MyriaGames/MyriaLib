using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Services;
using Xunit;

namespace Myria.Lib.Tests;

public class SkillLevelingServiceTests
{
    private static Skill MakeSkill(string id = "test_skill", float scalingFactor = 10f) => new()
    {
        Id = id,
        Name = id,
        Description = "",
        ManaCost = 5,
        Type = SkillType.Physical,
        Target = "SingleEnemy",
        ScalingFactor = scalingFactor,
        StatToScaleFrom = "ATK",
    };

    [Fact]
    public void GrantUsage_BelowFirstBreakpoint_StaysLevel1_NoPoints()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();

        for (int i = 0; i < SkillLevelingService.LevelBreakpoints[0].Uses - 1; i++)
            SkillLevelingService.GrantUsage(character, skill.Id);

        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);
        Assert.Equal(1, progress.Level);
        Assert.Equal(0, progress.UnspentPoints);
    }

    [Fact]
    public void GrantUsage_ReachingBreakpoint_LevelsUpAndGrantsPoints()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();

        int usesForLevel2 = SkillLevelingService.LevelBreakpoints[0].Uses;
        for (int i = 0; i < usesForLevel2; i++)
            SkillLevelingService.GrantUsage(character, skill.Id);

        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);
        Assert.Equal(2, progress.Level);
        Assert.Equal(SkillLevelingService.PointsPerLevel, progress.UnspentPoints);
    }

    [Fact]
    public void TrySpendPoint_WithNoUnspentPoints_Fails()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "more_power", ScalingFactorDelta = 2f });

        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "more_power", out var error);

        Assert.False(ok);
        Assert.Equal("no_points", error);
    }

    [Fact]
    public void TrySpendPoint_UnknownUpgradeId_Fails()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        LevelUpOnce(character, skill.Id);

        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "does_not_exist", out var error);

        Assert.False(ok);
        Assert.Equal("unknown_upgrade", error);
    }

    [Fact]
    public void TrySpendPoint_Valid_ConsumesPointAndRecordsPurchase()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "more_power", ScalingFactorDelta = 2f });
        LevelUpOnce(character, skill.Id);

        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "more_power", out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);
        Assert.Equal(0, progress.UnspentPoints);
        Assert.Contains("more_power", progress.PurchasedUpgradeIds);
    }

    [Fact]
    public void TrySpendPoint_RepeatableUpgrade_CanBePurchasedMultipleTimes()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "more_power", ScalingFactorDelta = 2f }); // no AddedEffects - unlimited by default
        LevelUpOnce(character, skill.Id);
        LevelUpOnce(character, skill.Id); // two points earned total

        Assert.True(SkillLevelingService.TrySpendPoint(character, skill, "more_power", out _));
        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "more_power", out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);
        Assert.Equal(2, progress.PurchasedUpgradeIds.Count(id => id == "more_power"));
        Assert.Equal(0, progress.UnspentPoints);
    }

    [Fact]
    public void TrySpendPoint_EffectGrantingUpgrade_DefaultsToMaxPurchasesOne_Fails()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption
        {
            Id = "adds_stun",
            AddedEffects = new List<SkillEffectEntry> { new SkillEffectEntry { EffectId = "stun_short", ApplyTo = EffectTarget.Target } }
        });
        LevelUpOnce(character, skill.Id);
        LevelUpOnce(character, skill.Id); // two points earned total

        Assert.True(SkillLevelingService.TrySpendPoint(character, skill, "adds_stun", out _));
        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "adds_stun", out var error);

        Assert.False(ok);
        Assert.Equal("max_purchases_reached", error);
    }

    [Fact]
    public void TrySpendPoint_BelowRequiredLevel_Fails()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "tier2_power", ScalingFactorDelta = 2f, RequiredLevel = 11 });
        LevelUpOnce(character, skill.Id); // level 2 - well below RequiredLevel 11

        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "tier2_power", out var error);

        Assert.False(ok);
        Assert.Equal("level_too_low", error);
    }

    [Fact]
    public void ResolveEffectiveSkill_NoProgress_AppliesFullBaseNerf()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill(scalingFactor: 10f); // ManaCost 5, Cooldown 0 (default)

        var effective = SkillLevelingService.ResolveEffectiveSkill(character, skill);

        Assert.NotSame(skill, effective); // level 1 is fully nerfed relative to the target/authored numbers
        Assert.Equal(10f, skill.ScalingFactor); // base template untouched
        Assert.Equal(5, skill.ManaCost);
        Assert.Equal(10f * (1f - SkillLevelingService.BaseScalingNerfFraction), effective.ScalingFactor, precision: 3);
        Assert.Equal((int)Math.Round(5 * (1f + SkillLevelingService.BaseManaCostSurchargeFraction)), effective.ManaCost);
        Assert.Equal(SkillLevelingService.BaseCooldownBonusTurns, effective.Cooldown);
    }

    [Fact]
    public void ResolveEffectiveSkill_AfterLevelingAndUpgrading_ReturnsDistinctBoostedClone_DoesNotMutateBase()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill(scalingFactor: 10f);
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "more_power", ScalingFactorDelta = 3f, ManaCostDelta = 1 });
        LevelUpOnce(character, skill.Id);
        Assert.True(SkillLevelingService.TrySpendPoint(character, skill, "more_power", out _));

        var effective = SkillLevelingService.ResolveEffectiveSkill(character, skill);
        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);

        Assert.NotSame(skill, effective);
        Assert.Equal(10f, skill.ScalingFactor); // base template untouched
        Assert.Equal(5, skill.ManaCost);

        // Same fade formula ResolveEffectiveSkill uses, at whatever level LevelUpOnce actually reached,
        // plus the purchased upgrade's own deltas on top.
        float fade = Math.Clamp((progress.Level - 1) / (float)(SkillLevelingService.NerfFadeReferenceLevel - 1), 0f, 1f);
        float expectedScaling = progress.Level < SkillLevelingService.NerfFadeReferenceLevel
            ? 10f * (1f - SkillLevelingService.BaseScalingNerfFraction * (1f - fade))
            : 10f * (1f + SkillLevelingService.BaselineScalingGrowthPerLevel * (progress.Level - SkillLevelingService.NerfFadeReferenceLevel));
        int expectedManaCost = progress.Level < SkillLevelingService.NerfFadeReferenceLevel
            ? (int)Math.Round(5 * (1f + SkillLevelingService.BaseManaCostSurchargeFraction * (1f - fade)))
            : 5;

        Assert.Equal(expectedScaling + 3f, effective.ScalingFactor, precision: 3);
        Assert.Equal(expectedManaCost + 1, effective.ManaCost);
        Assert.Equal(skill.Id, effective.Id);
    }

    [Fact]
    public void ResolveEffectiveSkill_PastReferenceLevel_GrowsBeyondTarget()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill(scalingFactor: 10f);

        // Level up well past NerfFadeReferenceLevel into the "continue upgrading" territory.
        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);
        progress.UsageCount = SkillLevelingService.LevelBreakpoints.First(b => b.Level == 12).Uses;
        SkillLevelingService.RecalculateLevelAndPoints(character, skill.Id);
        Assert.Equal(12, progress.Level);

        var effective = SkillLevelingService.ResolveEffectiveSkill(character, skill);

        // Beyond the reference level the base-nerf is fully gone and it grows past the target instead.
        float expected = 10f * (1f + SkillLevelingService.BaselineScalingGrowthPerLevel * (12 - SkillLevelingService.NerfFadeReferenceLevel));
        Assert.Equal(expected, effective.ScalingFactor, precision: 3);
        Assert.True(effective.ScalingFactor > 10f);
        Assert.Equal(5, effective.ManaCost); // no further mana discount past target from leveling alone
    }

    [Fact]
    public void Respec_ClearsPurchasedUpgrades_ButKeepsLevelAndUsage()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "more_power", ScalingFactorDelta = 2f });
        LevelUpOnce(character, skill.Id);
        Assert.True(SkillLevelingService.TrySpendPoint(character, skill, "more_power", out _));

        SkillLevelingService.Respec(character, skill.Id);

        var progress = SkillLevelingService.GetOrCreate(character, skill.Id);
        Assert.Empty(progress.PurchasedUpgradeIds);
        Assert.Equal(2, progress.Level);
        Assert.Equal(SkillLevelingService.PointsPerLevel, progress.UnspentPoints);
    }

    private static void LevelUpOnce(Myria.Lib.Core.Entities.Characters.Character character, string skillId)
    {
        var before = SkillLevelingService.GetOrCreate(character, skillId).Level;
        int guard = 0;
        while (SkillLevelingService.GetOrCreate(character, skillId).Level == before && guard++ < 10000)
            SkillLevelingService.GrantUsage(character, skillId);
    }
}
