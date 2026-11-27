using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Systems;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Public combat rules for Mono (MONO request 2026-11-27): seeded-Random overloads and the shared
/// skill helpers the solo, group and PvP fights now call.
/// </summary>
[Collection("GameData")]
public class CombatApiTests
{
    public CombatApiTests(GameDataFixture _) { }

    [Fact]
    public void SeededOverloads_AreReproducible()
    {
        var attacker = new TestCombatant { TotalAim = 50, TotalPhysicalAttack = 120, TotalDEX = 40 };
        var defender = new TestCombatant { TotalEvasion = 100, TotalPhysicalDefense = 60 };

        var r1 = new Random(42); var r2 = new Random(42);
        var first  = Enumerable.Range(0, 50).Select(_ => CombatSystem.CalculateDamageWithCrit(attacker, defender, r1)).ToList();
        var second = Enumerable.Range(0, 50).Select(_ => CombatSystem.CalculateDamageWithCrit(attacker, defender, r2)).ToList();

        Assert.Equal(first, second);
        Assert.Contains(first, d => d.Damage == 0);   // 50 aim vs 100 evasion: some misses
        Assert.Contains(first, d => d.Damage > 0);
    }

    [Fact]
    public void SkillDamage_IsRawSquaredOverRawPlusDefense()
    {
        var caster = new Character("C", new Stats()) { Class = "Fighter" };
        var skill = new Skill { Id = "x", ScalingFactor = 2f, StatToScaleFrom = "ATK", Type = SkillType.Physical };
        var target = new TestCombatant { TotalPhysicalDefense = 50 };

        float raw = caster.TotalPhysicalAttack * 2f;
        Assert.Equal(Math.Max(1, (int)(raw * raw / (raw + 50))), CombatSystem.SkillDamage(caster, skill, target));
        Assert.Equal(0, CombatSystem.SkillDamage(caster, new Skill { IsHealing = true, StatToScaleFrom = "SPR" }, target));
    }

    [Fact]
    public void SkillStats_MapIntToMatk_AndEffectsToThePrimaryStat()
    {
        var caster = new Character("C", new Stats()) { Class = "Druid" };
        var skill = new Skill { StatToScaleFrom = "INT", ScalingFactor = 1.5f, IsHealing = true };

        Assert.Equal(caster.TotalMagicAttack, CombatSystem.SkillBaseStat(caster, skill));
        Assert.Equal(caster.TotalINT, CombatSystem.SkillEffectStat(caster, skill));
        Assert.Equal((int)(caster.TotalMagicAttack * 1.5f), CombatSystem.SkillHeal(caster, skill));
    }
}
