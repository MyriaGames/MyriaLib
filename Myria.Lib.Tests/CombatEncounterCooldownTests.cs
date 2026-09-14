using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Regression coverage for a real bug: GetSkillCooldownRemaining used to subtract an extra 1 on
/// top of the Cooldown+1 storage offset, silently cutting every skill's effective cooldown one
/// turn short of what was authored (Cooldown: 2 only actually blocked for 1 turn). See
/// CombatEncounter's _skillCooldowns doc comment for the full explanation.
/// </summary>
public class CombatEncounterCooldownTests
{
    private static Monster MakeHarmlessMonster() =>
        new(1, "TestDummy", new Stats(), "", exp: 0);

    private static Skill MakeSkillWithCooldown(int cooldown) => new()
    {
        Id = "cd_skill",
        Name = "Cooldown Skill",
        Description = "",
        ManaCost = 0,
        Type = SkillType.Physical,
        Target = SkillTarget.Self,
        IsHealing = true,
        ScalingFactor = 1f,
        StatToScaleFrom = "ATK",
        Cooldown = cooldown
    };

    private static Character MakeSurvivableCharacter()
    {
        var character = TestHelpers.CreateCharacter(level: 10);
        character.Stats.BaseHealth = 1_000_000; // survive many rounds regardless of monster stats
        character.CurrentHealth = character.MaxHealth;
        return character;
    }

    [Fact]
    public void CooldownTwo_BlocksForExactlyTwoTurns_ThenBecomesAvailableAgain()
    {
        var character = MakeSurvivableCharacter();
        var monster = MakeHarmlessMonster();
        var skill = MakeSkillWithCooldown(cooldown: 2);
        var encounter = new CombatEncounter(character, monster);

        // Turn 1: first use always succeeds and puts it on cooldown.
        Assert.True(encounter.CharacterBeginCast(skill));
        Assert.Equal(2, encounter.GetSkillCooldownRemaining(skill.Id));

        // Turn 2: still on cooldown - must be blocked. A blocked cast doesn't consume a turn, so
        // advance via a basic attack instead (mirrors what a player does when their skill is greyed out).
        Assert.False(encounter.CharacterBeginCast(skill));
        encounter.CharacterAttack();
        Assert.Equal(1, encounter.GetSkillCooldownRemaining(skill.Id));

        // Turn 3: Cooldown: 2 means unusable for 2 full turns after use - still blocked here.
        // (This is the exact case the bug broke: it incorrectly allowed the skill again on turn 3.)
        Assert.False(encounter.CharacterBeginCast(skill));
        encounter.CharacterAttack();
        Assert.Equal(0, encounter.GetSkillCooldownRemaining(skill.Id));

        // Turn 4: cooldown has fully elapsed - usable again.
        Assert.True(encounter.CharacterBeginCast(skill));
    }

    [Fact]
    public void CooldownOne_BlocksForExactlyOneTurn()
    {
        var character = MakeSurvivableCharacter();
        var monster = MakeHarmlessMonster();
        var skill = MakeSkillWithCooldown(cooldown: 1);
        var encounter = new CombatEncounter(character, monster);

        Assert.True(encounter.CharacterBeginCast(skill));
        Assert.Equal(1, encounter.GetSkillCooldownRemaining(skill.Id));

        Assert.False(encounter.CharacterBeginCast(skill));
        encounter.CharacterAttack();
        Assert.Equal(0, encounter.GetSkillCooldownRemaining(skill.Id));

        Assert.True(encounter.CharacterBeginCast(skill));
    }

    [Fact]
    public void ZeroCooldown_NeverBlocks()
    {
        var character = MakeSurvivableCharacter();
        var monster = MakeHarmlessMonster();
        var skill = MakeSkillWithCooldown(cooldown: 0);
        var encounter = new CombatEncounter(character, monster);

        Assert.True(encounter.CharacterBeginCast(skill));
        Assert.Equal(0, encounter.GetSkillCooldownRemaining(skill.Id));
        Assert.True(encounter.CharacterBeginCast(skill));
    }
}
