using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Regestries;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Gear set effects (v0.3): two pieces of a set give the first skill bonus, the third piece adds
/// more; one piece gives nothing, and two sets count separately.
/// </summary>
[Collection("GameData")]
public class ItemSetBonusTests
{
    public ItemSetBonusTests(GameDataFixture _) { }

    private static EquipmentItem Piece(string id) => (EquipmentItem)ItemFactory.CreateItem(id)!;

    private static Myria.Lib.Core.Entities.Characters.Character Fighter()
    {
        var c = TestHelpers.CreateCharacter("Setter", 15);
        c.Class = "Fighter";
        return c;
    }

    private static Myria.Lib.Core.Entities.Skills.Skill Effective(Myria.Lib.Core.Entities.Characters.Character c, string id) =>
        SkillLevelingService.ResolveEffectiveSkill(c, SkillFactory.GetSkill(id)!);

    [Fact]
    public void OnePiece_GivesNothing()
    {
        var c = Fighter();
        var before = Effective(c, "heavy_blow").ScalingFactor;
        c.WeaponSlot = Piece("siltshell_cleaver");
        Assert.Equal(before, Effective(c, "heavy_blow").ScalingFactor, 3);
    }

    [Fact]
    public void TwoPieces_GiveTheFirstBonus_TheThirdAddsMore()
    {
        var c = Fighter();
        var raw = SkillFactory.GetSkill("heavy_blow")!;
        float baseScaling = Effective(c, "heavy_blow").ScalingFactor;
        int baseMana = Effective(c, "heavy_blow").ManaCost;
        float slamBase = Effective(c, "slam").ScalingFactor;

        c.WeaponSlot = Piece("siltshell_cleaver");
        c.ArmorSlot = Piece("siltshell_plate");
        Assert.Equal(baseScaling + raw.ScalingFactor * 0.10f, Effective(c, "heavy_blow").ScalingFactor, 3);
        Assert.Equal(baseMana, Effective(c, "heavy_blow").ManaCost);
        Assert.Equal(slamBase, Effective(c, "slam").ScalingFactor, 3);

        c.AccessorySlot = Piece("siltshell_band");
        Assert.Equal(baseMana + (int)Math.Round(raw.ManaCost * -0.20), Effective(c, "heavy_blow").ManaCost);
        Assert.True(Effective(c, "slam").ScalingFactor > slamBase);
    }

    [Fact]
    public void MixedSets_CountSeparately()
    {
        var c = Fighter();
        float rush = Effective(c, "battle_rush").ScalingFactor;
        float blow = Effective(c, "heavy_blow").ScalingFactor;
        c.WeaponSlot = Piece("breswelc_greatblade");
        c.ArmorSlot = Piece("siltshell_plate");
        c.AccessorySlot = Piece("breswelc_scale_charm");

        Assert.True(Effective(c, "battle_rush").ScalingFactor > rush);       // Breswelc 2 pieces
        Assert.Equal(blow, Effective(c, "heavy_blow").ScalingFactor, 3);     // Siltshell only 1
    }

    /// <summary>Every set covers every selectable class at both tiers with skills that exist.</summary>
    [Fact]
    public void EverySet_BoostsEveryClass_AtBothTiers()
    {
        string[] classes = { "Knight", "Barbarian", "Fighter", "SoulsKnight", "Rogue", "Archer", "Hunter", "Cleric", "ArcanMage", "ElementalMage", "Druid" };
        Assert.Equal(2, ItemSetRegistry.All.Count);
        foreach (var set in ItemSetRegistry.All)
        {
            Assert.Equal(8, set.Items.Count);
            Assert.All(set.Items, id => Assert.Same(set, ItemSetRegistry.SetOf(id)));
            foreach (var tier in set.Tiers)
                foreach (var cls in classes)
                    Assert.Contains(tier.Bonuses, b => SkillFactory.GetSkill(b.SkillId)?.Class == cls);
        }
    }
}
