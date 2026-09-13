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
    public void TrySpendPoint_AlreadyPurchased_Fails()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "more_power", ScalingFactorDelta = 2f });
        skill.UpgradeOptions.Add(new SkillUpgradeOption { Id = "less_mana", ManaCostDelta = -1 });
        LevelUpOnce(character, skill.Id);
        LevelUpOnce(character, skill.Id); // reach level 3 - two points earned total, so a second spend attempt fails for the right reason (duplicate), not lack of points

        Assert.True(SkillLevelingService.TrySpendPoint(character, skill, "more_power", out _));
        bool ok = SkillLevelingService.TrySpendPoint(character, skill, "more_power", out var error);

        Assert.False(ok);
        Assert.Equal("already_purchased", error);
    }

    [Fact]
    public void ResolveEffectiveSkill_NoProgress_ReturnsSameInstance()
    {
        var character = TestHelpers.CreateCharacter();
        var skill = MakeSkill();

        var effective = SkillLevelingService.ResolveEffectiveSkill(character, skill);

        Assert.Same(skill, effective);
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

        Assert.NotSame(skill, effective);
        Assert.Equal(10f, skill.ScalingFactor); // base template untouched
        Assert.Equal(5, skill.ManaCost);
        // level 2 baseline growth (5% of base 10 = 0.5) + the purchased upgrade's +3
        Assert.Equal(10f + 0.5f + 3f, effective.ScalingFactor, precision: 3);
        Assert.Equal(6, effective.ManaCost);
        Assert.Equal(skill.Id, effective.Id);
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
