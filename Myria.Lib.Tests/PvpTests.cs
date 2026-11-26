using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>PvP (v0.3): the PvpEncounter engine and PvpRatingService stakes.</summary>
public class PvpTests
{
    private static Character Fighter(string name, int hp = 100_000)
    {
        var c = TestHelpers.CreateCharacter(name, level: 10);
        c.Stats.BaseHealth = Math.Max(hp, 1_000_000);
        c.CurrentHealth = hp;
        c.CurrentMana = c.MaxMana;
        return c;
    }

    private static Character Current(PvpEncounter enc) =>
        enc.Find(enc.CurrentTurnCharacterName)!;

    // ── Turns and targeting ───────────────────────────────────────────────

    [Fact]
    public void OnlyTheCurrentParticipantCanAct()
    {
        var a = Fighter("Alpha"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a }, new[] { b });
        var waiting = Current(enc) == a ? b : a;

        Assert.False(enc.Attack(waiting.Name, 0));
        Assert.True(enc.Attack(Current(enc).Name, 0));
    }

    [Fact]
    public void Attack_PassesTheTurnToTheOtherSide()
    {
        var a = Fighter("Alpha"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a }, new[] { b });
        var first = Current(enc);

        enc.Attack(first.Name, 0);

        Assert.NotEqual(first.Name, enc.CurrentTurnCharacterName);
    }

    [Fact]
    public void FightEndsWhenOneSideIsDown_WithWinnersAndLosers()
    {
        var a = Fighter("Alpha"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a }, new[] { b });
        a.CurrentHealth = 1; b.CurrentHealth = 1; // first landed hit decides it

        for (int i = 0; i < 200 && !enc.IsFinished; i++)
            enc.Attack(enc.CurrentTurnCharacterName, 0);

        Assert.True(enc.IsFinished);
        var winner = enc.WinningSide == 1 ? a : b;
        var loser  = winner == a ? b : a;
        Assert.Equal(new[] { winner }, enc.Winners);
        Assert.Equal(new[] { loser }, enc.Losers);
        Assert.False(loser.IsAlive);
        Assert.Contains(enc.Log, e => e.Key == "pg.fight.pvp.won");
    }

    [Fact]
    public void DownedOpponent_CantBeTargeted()
    {
        var a = Fighter("Alpha"); var b1 = Fighter("Beta1"); var b2 = Fighter("Beta2");
        var enc = new PvpEncounter(new[] { a }, new[] { b1, b2 });
        b1.CurrentHealth = 0;

        while (Current(enc) != a) enc.Attack(enc.CurrentTurnCharacterName, 0);

        Assert.False(enc.Attack(a.Name, 0)); // b1 is down
        Assert.True(enc.Attack(a.Name, 1));
    }

    [Fact]
    public void SingleAllyHeal_TargetsTheCastersOwnSide()
    {
        var a1 = Fighter("Alpha1"); var a2 = Fighter("Alpha2"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a1, a2 }, new[] { b });
        a2.CurrentHealth = 10;
        var heal = new Skill
        {
            Id = "test_heal", Name = "Test Heal", Description = "", ManaCost = 0,
            Type = SkillType.Magical, Target = SkillTarget.SingleAlly, IsHealing = true,
            ScalingFactor = 5f, StatToScaleFrom = "SPR",
        };

        while (Current(enc) != a1) enc.Attack(enc.CurrentTurnCharacterName, 0);
        int bHp = b.CurrentHealth;

        Assert.True(enc.CastSkill(a1.Name, heal, targetIndex: 1)); // index into a1's own side
        Assert.True(a2.CurrentHealth > 10);
        Assert.Equal(bHp, b.CurrentHealth);
    }

    [Fact]
    public void UseItem_ConsumesFromTheActorsInventory()
    {
        var a = Fighter("Alpha"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a }, new[] { b });
        var actor = Current(enc);
        var potion = new ConsumableItem { Id = "p", Name = "item.p", Description = "", StackSize = 2, MaxStackSize = 10, HealAmount = 5 };
        actor.Inventory.Items.Add(potion);

        Assert.True(enc.UseItem(actor.Name, potion));
        Assert.Equal(1, potion.StackSize);
        Assert.NotEqual(actor.Name, enc.CurrentTurnCharacterName);
    }

    // ── Flee and stalled turns ────────────────────────────────────────────

    [Fact]
    public void LastFighterOfASideFleeing_GivesTheOtherSideTheWin_AndCountsAsNeither()
    {
        var a = Fighter("Alpha"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a }, new[] { b });

        Assert.True(enc.Flee(b.Name));

        Assert.True(enc.IsFinished);
        Assert.Equal(1, enc.WinningSide);
        Assert.Equal(new[] { a }, enc.Winners);
        Assert.Empty(enc.Losers); // the forfeit is settled at flee time, not again at the end
    }

    [Fact]
    public void FleeingOutOfTurn_DoesntMoveTheTurn()
    {
        var a1 = Fighter("Alpha1"); var a2 = Fighter("Alpha2"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a1, a2 }, new[] { b });
        var current = Current(enc);
        var other = new[] { a1, a2 }.First(c => c != current); // its side keeps a fighter either way

        enc.Flee(other.Name);

        Assert.False(enc.IsFinished);
        Assert.Equal(current.Name, enc.CurrentTurnCharacterName);
    }

    [Fact]
    public void StalledTurn_CanOnlyBeSkippedAfterTheTimeout()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var a = Fighter("Alpha"); var b = Fighter("Beta");
        var enc = new PvpEncounter(new[] { a }, new[] { b }, () => now);
        var stalled = enc.CurrentTurnCharacterName;

        now += PvpEncounter.TurnTimeout - TimeSpan.FromSeconds(1);
        Assert.False(enc.SkipStalledTurn());
        Assert.Equal(stalled, enc.CurrentTurnCharacterName);

        now += TimeSpan.FromSeconds(1);
        Assert.True(enc.SkipStalledTurn());
        Assert.NotEqual(stalled, enc.CurrentTurnCharacterName);
        Assert.Contains(enc.Log, e => e.Key == "pg.fight.pvp.turnSkipped");
    }

    [Fact]
    public void RestorePreFightState_RestoresVitalsAndClearsFightEffects()
    {
        var a = Fighter("Alpha", hp: 500); var b = Fighter("Beta");
        int mp = a.CurrentMana;
        var enc = new PvpEncounter(new[] { a }, new[] { b });

        a.CurrentHealth = 0;
        a.CurrentMana = 0;
        a.ActiveEffects.Add(new ActiveEffect { DefinitionId = "poison_weak", Type = EffectType.Poison, TurnsRemaining = 3 });

        enc.RestorePreFightState(a);

        Assert.Equal(500, a.CurrentHealth);
        Assert.Equal(mp, a.CurrentMana);
        Assert.Empty(a.ActiveEffects);
    }

    [Fact]
    public void SameCharacterOnBothSides_IsRejected()
    {
        var a = Fighter("Alpha");
        Assert.Throws<ArgumentException>(() => new PvpEncounter(new[] { a }, new[] { a }));
    }

    // ── Rating ────────────────────────────────────────────────────────────

    [Fact]
    public void EvenlyRatedFight_MovesHalfTheKFactor_AndPaysTheWinner()
    {
        var w = Fighter("W"); var l = Fighter("L");

        var results = PvpRatingService.ApplyFightResult(new[] { w }, new[] { l });

        Assert.Equal(PvpRatingService.StartingRating + PvpRatingService.KFactor / 2, w.PvpRating);
        Assert.Equal(PvpRatingService.StartingRating - PvpRatingService.KFactor / 2, l.PvpRating);
        Assert.Equal(PvpRatingService.WinCurrency, w.PvpCurrency);
        Assert.Equal(PvpRatingService.LossCurrency, l.PvpCurrency);
        Assert.Equal((1, 0), (w.PvpWins, w.PvpLosses));
        Assert.Equal((0, 1), (l.PvpWins, l.PvpLosses));
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void FavouriteWinning_GainsLessThanAnUpset()
    {
        int favouriteGain = PvpRatingService.RatingChange(1400, 1000);
        int upsetGain     = PvpRatingService.RatingChange(1000, 1400);

        Assert.True(favouriteGain < PvpRatingService.KFactor / 2);
        Assert.True(upsetGain > PvpRatingService.KFactor / 2);
        Assert.True(favouriteGain >= PvpRatingService.MinRatingChange);
    }

    [Fact]
    public void Rating_NeverDropsBelowZero()
    {
        // Evenly matched low ratings: the swing (K/2) is bigger than the loser's 3 points.
        var w = Fighter("W"); var l = Fighter("L");
        w.PvpRating = 3;
        l.PvpRating = 3;

        PvpRatingService.ApplyFightResult(new[] { w }, new[] { l });

        Assert.Equal(0, l.PvpRating);
    }

    [Fact]
    public void WholeLosingSideFled_WinnersAreRatedAgainstThatSide_NotADefault()
    {
        var w = Fighter("W"); var fled = Fighter("F");
        fled.PvpRating = 1400; // beating a much stronger side that ran should pay like an upset

        var results = PvpRatingService.ApplyFightResult(new[] { w }, Array.Empty<Character>(), losingSide: new[] { fled });

        Assert.Equal(PvpRatingService.StartingRating + PvpRatingService.RatingChange(1000, 1400), w.PvpRating);
        Assert.Single(results);
    }

    [Fact]
    public void Forfeit_IsALossAgainstTheOpposingSide()
    {
        var quitter = Fighter("Q"); var opp = Fighter("O");

        var r = PvpRatingService.ApplyForfeit(quitter, new[] { opp });

        Assert.False(r.Won);
        Assert.True(r.RatingDelta < 0);
        Assert.Equal(1, quitter.PvpLosses);
        Assert.Equal(PvpRatingService.StartingRating, opp.PvpRating); // opponents settle at the end
    }
}
