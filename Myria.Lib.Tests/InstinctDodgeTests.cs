using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Systems;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 Instinct passive: players and monsters share one hit rule (aim at or above evasion always
/// hits, below it aim / evasion), but the Rogue and Hunter always keep a dodge chance -
/// instinct × evasion / aim, capped at the instinct - so evasion still counts against bosses.
/// </summary>
[Collection("GameData")]
public class InstinctDodgeTests
{
    public InstinctDodgeTests(GameDataFixture _) { }

    [Fact]
    public void WithoutInstinct_AimAtOrAboveEvasion_AlwaysHits()
    {
        Assert.Equal(1f, CombatSystem.HitChance(100, 100));
        Assert.Equal(1f, CombatSystem.HitChance(300, 100));
        Assert.Equal(0.5f, CombatSystem.HitChance(50, 100), precision: 4);
    }

    [Fact]
    public void Instinct_KeepsADodgeChance_PastTheGuaranteedPoint()
    {
        // Rogue (25%) with 141 evasion against the Rootmother's 302 aim: 25% × 141/302 ≈ 11.7% dodge.
        Assert.Equal(1f - 0.25f * 141f / 302f, CombatSystem.HitChance(302, 141, 0.25f), precision: 4);
        // At aim = evasion it is the full instinct, so the curve has no jump there.
        Assert.Equal(0.75f, CombatSystem.HitChance(100, 100, 0.25f), precision: 4);
    }

    [Fact]
    public void Instinct_NeverLowersTheNormalDodge()
    {
        // Low-aim attacker: the normal rule (60% dodge) is better than the instinct, so it wins.
        Assert.Equal(0.4f, CombatSystem.HitChance(40, 100, 0.25f), precision: 4);
        Assert.Equal(0f, CombatSystem.HitChance(0, 100, 0.25f), precision: 4);      // aim 0 never hits
    }

    [Fact]
    public void WithoutInstinct_TryHitStaysGuaranteed()
    {
        var attacker = new TestCombatant { TotalAim = 500 };
        var defender = new TestCombatant { TotalEvasion = 10 };
        Assert.Equal(0f, ((Myria.Lib.Core.Systems.Interfaces.ICombatant)defender).InstinctDodge);
        Assert.True(CombatSystem.TryHit(attacker, defender));   // no instinct: guaranteed
    }

    [Fact]
    public void OnlyTheRogueAndHunterHaveInstinct()
    {
        Assert.Equal(0.25f, ClassProfile.All["Rogue"].InstinctDodge);
        Assert.Equal(0.20f, ClassProfile.All["Hunter"].InstinctDodge);
        Assert.All(ClassProfile.All.Where(p => p.Key is not ("Rogue" or "Hunter")),
            p => Assert.Equal(0f, p.Value.InstinctDodge));
    }

    [Fact]
    public void ACharactersInstinct_ComesFromTheirClass()
    {
        Assert.Equal(0.25f, new Character("R", new Myria.Lib.Core.Entities.Stats()) { Class = "Rogue" }.InstinctDodge);
        Assert.Equal(0f, new Character("A", new Myria.Lib.Core.Entities.Stats()) { Class = "Archer" }.InstinctDodge);
    }
}
