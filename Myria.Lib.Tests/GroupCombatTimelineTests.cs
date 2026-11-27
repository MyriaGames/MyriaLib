using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Systems;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Group combat turn timeline (v0.3): players and monsters share one timeline. Monsters act at
/// normal speed; a player's speed is measured against the party's average DEX (softened curve,
/// capped), so within a party the fast act more often and the slow less, interleaved with the
/// monsters - no back-to-back bonus actions any more.
/// </summary>
public class GroupCombatTimelineTests
{
    private static Monster Dummy(int dex = 50)
    {
        var m = new Monster(1, "Dummy", new Stats { BaseHealth = 1_000_000, Dexterity = dex, Strength = 1 }, "", exp: 0);
        m.ResetHealth();
        return m;
    }

    private static Myria.Lib.Core.Entities.Characters.Character Hero(string name, int dex)
    {
        var c = TestHelpers.CreateCharacter(name, 10);
        c.Stats.Dexterity = dex;
        c.Stats.BaseHealth = 1_000_000; c.CurrentHealth = c.MaxHealth;
        return c;
    }

    private static int MonsterTurnsIn(GroupCombatEncounter enc, int from) =>
        enc.Log.Skip(from).Count(e => e.Key is "pg.fight.log.enemyHit" or "pg.fight.log.enemyMiss");

    [Fact]
    public void Solo_PlayerAndMonster_TakeTurnsInTurn_WhateverTheirDex()
    {
        foreach (var (heroDex, monsterDex) in new[] { (50, 50), (200, 20), (20, 200) })
        {
            var hero = Hero("Hero", heroDex);
            var enc = new GroupCombatEncounter(new[] { hero }, new List<Monster> { Dummy(monsterDex) });
            int start = enc.Log.Count;
            for (int i = 0; i < 30; i++) Assert.True(enc.CharacterAttack(enc.CurrentTurnCharacterName, 0));
            Assert.InRange(MonsterTurnsIn(enc, start), 28, 32);
        }
    }

    [Fact]
    public void InAParty_TheFasterPlayer_ActsMoreOften_AndTurnsInterleave()
    {
        var fast = Hero("Fast", 200);
        var slow = Hero("Slow", 50);
        var enc = new GroupCombatEncounter(new[] { fast, slow }, new List<Monster> { Dummy() });
        int fastTurns = 0, slowTurns = 0, longestRun = 0, run = 0;
        string last = "";
        for (int i = 0; i < 90; i++)
        {
            string who = enc.CurrentTurnCharacterName;
            if (who == fast.Name) fastTurns++; else slowTurns++;
            run = who == last ? run + 1 : 1; longestRun = Math.Max(longestRun, run); last = who;
            Assert.True(enc.CharacterAttack(who, 0));
        }

        // 4x the slow one's DEX: about twice the turns (softened, capped), never all of them.
        double ratio = (double)fastTurns / slowTurns;
        Assert.InRange(ratio, 1.5, 2.4);
        Assert.True(longestRun <= 3, $"longest run of player turns without the other: {longestRun}");
    }

    [Fact]
    public void Monsters_ActAtNormalSpeed_InAParty()
    {
        var a = Hero("A", 60);
        var b = Hero("B", 60);
        var enc = new GroupCombatEncounter(new[] { a, b }, new List<Monster> { Dummy(500) });
        int start = enc.Log.Count;
        for (int i = 0; i < 40; i++) Assert.True(enc.CharacterAttack(enc.CurrentTurnCharacterName, 0));
        Assert.InRange(MonsterTurnsIn(enc, start), 18, 22);   // one monster turn per two player turns
    }

    [Fact]
    public void NoBonusActions_AreLoggedAnyMore()
    {
        var enc = new GroupCombatEncounter(new[] { Hero("Fast", 300), Hero("Slow", 20) }, new List<Monster> { Dummy(10) });
        for (int i = 0; i < 20 && !enc.IsFinished; i++) enc.CharacterAttack(enc.CurrentTurnCharacterName, 0);
        Assert.DoesNotContain(enc.Log, e => e.Key == "pg.fight.log.bonusTurn");
    }
}
