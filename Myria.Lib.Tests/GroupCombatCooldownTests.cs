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

    /// <summary>
    /// A cooldown counts TURNS, not actions: a DEX bonus action used to tick it too, so a skill
    /// came back a round early (often right as the bonus action killed a monster).
    /// </summary>
    [Fact]
    public void Cooldown_CountsTurns_BonusActionsDontShortenIt()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        ch.Stats.BaseHealth = 1_000_000; ch.CurrentHealth = ch.MaxHealth;
        var enc = new GroupCombatEncounter(new[] { ch }, new List<Monster> { Dummy(1_000_000) });
        var skill = CooldownSkill(2);

        int BonusIn(int from) => enc.Log.Skip(from).Where(e => e.Key == "pg.fight.log.bonusTurn")
            .Sum(e => Convert.ToInt32(e.Args[1]));
        int round = 1, left = 1 + BonusIn(0), bonusActions = 0;
        var castRounds = new List<int>();
        for (int step = 0; step < 60 && !enc.IsFinished; step++)
        {
            int before = enc.Log.Count;
            if (enc.CharacterCastSkill(ch.Name, skill, 0)) castRounds.Add(round);
            else Assert.True(enc.CharacterAttack(ch.Name, 0));
            if (--left > 0) { bonusActions++; continue; }
            round++;
            left = 1 + BonusIn(before);
        }

        Assert.True(bonusActions > 0, "the test needs bonus actions to happen");
        Assert.True(castRounds.Count >= 3);
        for (int k = 1; k < castRounds.Count; k++)
            Assert.Equal(3, castRounds[k] - castRounds[k - 1]);   // used, 2 turns blocked, usable
    }
}
