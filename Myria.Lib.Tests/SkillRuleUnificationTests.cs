using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 (user decision 2026-11-27): solo fights use the same skill damage rule as group and PvP
/// fights, and lifesteal / mana siphon heal the caster once per skill, not once per enemy hit.
/// </summary>
[Collection("GameData")]
public class SkillRuleUnificationTests
{
    public SkillRuleUnificationTests(GameDataFixture _) { }

    private static Monster Dummy()
    {
        var m = new Monster(1, "Dummy", new Stats { BaseHealth = 1_000_000 }, "", exp: 0);
        m.ResetHealth();
        return m;
    }

    private static Skill Drain(string target, string effectId) => new()
    {
        Id = "drain_test", Name = "Drain", Description = "", ManaCost = 0,
        Type = SkillType.Magical, Target = target, ScalingFactor = 1f, StatToScaleFrom = "MATK",
        Effects = { new SkillEffectEntry { EffectId = effectId } },
    };

    [Fact]
    public void SoloSkill_DealsSkillDamage()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        var enemy = Dummy();
        var enc = new CombatEncounter(ch, enemy);
        var skill = new Skill { Id = "hit", Name = "Hit", Description = "", Type = SkillType.Physical,
            Target = SkillTarget.SingleEnemy, ScalingFactor = 1.5f, StatToScaleFrom = "ATK" };

        int expected = CombatSystem.SkillDamage(ch, skill, enemy);
        int before = enemy.CurrentHealth;
        Assert.True(enc.CharacterBeginCast(skill));

        Assert.Equal(expected, before - enemy.CurrentHealth);
    }

    [Fact]
    public void AreaLifesteal_HealsOnce_FromTheTotalDamage()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        ch.Stats.BaseHealth = 1_000_000;
        ch.CurrentHealth = ch.MaxHealth / 2;   // monsters may act first; the heal is read from the log
        var monsters = new List<Monster> { Dummy(), Dummy(), Dummy() };
        var enc = new GroupCombatEncounter(new[] { ch }, monsters);
        var skill = Drain(SkillTarget.AllEnemies, "lifesteal_soul");

        Assert.True(enc.CharacterCastSkill(ch.Name, skill, 0));

        int total = monsters.Sum(m => m.MaxHealth - m.CurrentHealth);
        var effect = EffectFactory.CreateEffect("lifesteal_soul", skill.Id, skill.ScalingFactor, CombatSystem.SkillEffectStat(ch, skill))!;
        // Read the heal from the log: the monsters act after the cast and change the caster's HP.
        var entry = Assert.Single(enc.Log, e => e.Key == "pg.fight.effect.lifesteal");
        Assert.Equal(Math.Max(1, (int)(total * effect.Magnitude)), (int)entry.Args[1]);
        Assert.All(monsters, m => Assert.True(m.CurrentHealth < m.MaxHealth));   // all three were hit
    }

    [Fact]
    public void AreaManaSiphon_RestoresOnce()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        ch.CurrentMana = 0;
        var enc = new GroupCombatEncounter(new[] { ch }, new List<Monster> { Dummy(), Dummy() });

        Assert.True(enc.CharacterCastSkill(ch.Name, Drain(SkillTarget.AllEnemies, "mana_siphon_arcane"), 0));
        Assert.Single(enc.Log, e => e.Key == "pg.fight.effect.manasiphon");
    }
}
