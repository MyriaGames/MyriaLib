using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// A skill's cooldown lasts for the whole group fight - killing the monster it was used on
/// (e.g. with a basic attack) must not make it usable again against the next one.
/// </summary>
public class GroupCombatCooldownTests
{
    private static Skill CooldownSkill(int cooldown) => new()
    {
        Id = "cd_skill", Name = "Cooldown Skill", Description = "", ManaCost = 0,
        Type = SkillType.Physical, Target = SkillTarget.SingleEnemy,
        ScalingFactor = 0.01f, StatToScaleFrom = "ATK", Cooldown = cooldown,
    };

    private static Monster Dummy(int health)
    {
        var stats = new Stats { BaseHealth = health };
        var m = new Monster(1, "TestDummy", stats, "", exp: 0);
        m.ResetHealth();
        return m;
    }

    [Fact]
    public void Cooldown_CarriesOverToTheNextMonster_AfterTheFirstDies()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        ch.Stats.BaseHealth = 1_000_000; ch.CurrentHealth = ch.MaxHealth;
        var first = Dummy(1);            // one basic attack kills it
        var second = Dummy(1_000_000);   // survives the whole test

        var enc = new GroupCombatEncounter(new[] { ch }, new List<Monster> { first, second });
        var skill = CooldownSkill(3);
        first.CurrentHealth = 1;

        Assert.True(enc.CharacterCastSkill(ch.Name, skill, 1));
        Assert.True(enc.CharacterAttack(ch.Name, 0));
        Assert.False(first.IsAlive);

        Assert.False(enc.CharacterCastSkill(ch.Name, skill, 1));   // still 2 actions to go
    }

    /// <summary>A cooldown counts the character's own turns: with cooldown 2 a skill is usable on
    /// every third own turn, however the turns interleave with the monsters'.</summary>
    [Fact]
    public void Cooldown_CountsTheCharactersOwnTurns()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        ch.Stats.BaseHealth = 1_000_000; ch.CurrentHealth = ch.MaxHealth;
        var enc = new GroupCombatEncounter(new[] { ch }, new List<Monster> { Dummy(1_000_000) });
        var skill = CooldownSkill(2);

        var castTurns = new List<int>();
        for (int turn = 1; turn <= 30 && !enc.IsFinished; turn++)
        {
            Assert.Equal(ch.Name, enc.CurrentTurnCharacterName);
            if (enc.CharacterCastSkill(ch.Name, skill, 0)) castTurns.Add(turn);
            else Assert.True(enc.CharacterAttack(ch.Name, 0));
        }

        Assert.True(castTurns.Count >= 5);
        for (int k = 1; k < castTurns.Count; k++)
            Assert.Equal(3, castTurns[k] - castTurns[k - 1]);   // used, 2 turns blocked, usable
    }
}
