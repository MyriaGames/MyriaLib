using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Gear power curves (v0.3): a flatter upgrade curve (+10 = x2.7, was x11) and a convex craft
/// quality (x3 only for a master crafter, little for a novice), separate from gathering's linear
/// skill multiplier.
/// </summary>
public class UpgradeCurveTests
{
    [Theory]
    [InlineData(0, 1.0f)]
    [InlineData(1, 1.1f)]
    [InlineData(4, 1.45f)]
    [InlineData(6, 1.75f)]
    [InlineData(10, 2.7f)]
    public void UpgradeMultiplier_FollowsTheFlatCurve(int level, float expected) =>
        Assert.Equal(expected, EquipmentItem.UpgradeMultiplier(level), 3);

    [Fact]
    public void UpgradeMultiplier_RisesWithEveryLevel()
    {
        for (int i = 1; i <= 12; i++)
            Assert.True(EquipmentItem.UpgradeMultiplier(i) > EquipmentItem.UpgradeMultiplier(i - 1), $"+{i}");
    }

    [Fact]
    public void CraftQuality_IsOneForANovice_AndThreeForAMaster()
    {
        Assert.Equal(1.0, JobXpService.GetCraftQuality(1), 3);
        Assert.Equal(1.0 + JobXpService.MaxSkillBonus, JobXpService.GetCraftQuality(JobXpService.MaxLevel), 3);
    }

    [Fact]
    public void CraftQuality_GrowsSlowerThanTheGatherMultiplier_BelowMax()
    {
        foreach (int level in new[] { 10, 30, 50, 80 })
            Assert.True(JobXpService.GetCraftQuality(level) < JobXpService.GetSkillMultiplier(level), $"level {level}");
        Assert.True(JobXpService.GetCraftQuality(10) < 1.1);
    }

    [Fact]
    public void MasterCraftedMaxGear_IsFarStrongerThanNormalMaxGear()
    {
        double normal = EquipmentItem.UpgradeMultiplier(10);
        double master = normal * JobXpService.GetCraftQuality(JobXpService.MaxLevel);
        double novice = normal * JobXpService.GetCraftQuality(10);
        Assert.True(master > 2.5 * normal);
        Assert.True(novice < 1.1 * normal);
    }
}
