using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Using consumables as a combat action (v0.3 "Use items/potions in fights"), solo and group.
/// Also covers a bug the solo path used to have: it removed the whole stack via
/// Inventory.RemoveItem, so drinking one potion out of a stack of 5 deleted all 5.
/// </summary>
public class CombatItemUseTests
{
    private const int PotionHeal = 25;

    private static Monster MakeHarmlessMonster() =>
        new(1, "TestDummy", new Stats(), "", exp: 0);

    private static Character MakeWoundedCharacter(string name = "TestChar")
    {
        var character = TestHelpers.CreateCharacter(name, level: 10);
        character.Stats.BaseHealth = 1_000_000; // survive any number of monster turns
        character.CurrentHealth = 100;          // far below max, so a potion's heal is never capped
        return character;
    }

    private static ConsumableItem GivePotions(Character character, int count)
    {
        var potion = new ConsumableItem
        {
            Id = "t1_healing_potion",
            Name = "item.t1_healing_potion",
            Description = "",
            MaxStackSize = 100,
            StackSize = count,
            HealAmount = PotionHeal,
        };
        character.Inventory.Items.Add(potion);
        return potion;
    }

    // ── Inventory.ConsumeOne ──────────────────────────────────────────────

    [Fact]
    public void ConsumeOne_DecrementsAStack_AndRemovesTheLastUnit()
    {
        var character = TestHelpers.CreateCharacter();
        var potion = GivePotions(character, 2);

        Assert.True(character.Inventory.ConsumeOne(potion));
        Assert.Equal(1, potion.StackSize);
        Assert.Contains(potion, character.Inventory.Items);

        Assert.True(character.Inventory.ConsumeOne(potion));
        Assert.DoesNotContain(potion, character.Inventory.Items);
    }

    [Fact]
    public void ConsumeOne_ItemNotInInventory_ReturnsFalse()
    {
        var character = TestHelpers.CreateCharacter();
        var stranger = new ConsumableItem { Id = "x", Name = "x", Description = "" };

        Assert.False(character.Inventory.ConsumeOne(stranger));
    }

    // ── Solo ──────────────────────────────────────────────────────────────

    [Fact]
    public void Solo_UseItem_ConsumesOneFromStack_AndCostsOneTurn()
    {
        var character = MakeWoundedCharacter();
        var potion = GivePotions(character, 5);
        var encounter = new CombatEncounter(character, MakeHarmlessMonster());

        Assert.True(encounter.CharacterUseItem(potion));

        Assert.Equal(4, potion.StackSize); // one used, not the whole stack
        Assert.Contains(encounter.Log, e => e.Key == "pg.fight.log.usedItem");
        // Same cost as an attack: the enemy took its turn and it's the character's turn again,
        // with no extra recovery turn on top.
        Assert.Equal(CombatPhase.CharacterTurn, encounter.Phase);
        Assert.Equal(0, encounter.RecoveryTurnsRemaining);
    }

    [Fact]
    public void Solo_UseLastPotion_RemovesItFromInventory()
    {
        var character = MakeWoundedCharacter();
        var potion = GivePotions(character, 1);
        var encounter = new CombatEncounter(character, MakeHarmlessMonster());

        Assert.True(encounter.CharacterUseItem(potion));
        Assert.DoesNotContain(potion, character.Inventory.Items);
    }

    [Fact]
    public void Solo_ItemNotInInventory_IsRejectedWithoutSpendingTheTurn()
    {
        var character = MakeWoundedCharacter();
        var notOwned = new ConsumableItem { Id = "t1_healing_potion", Name = "p", Description = "", HealAmount = PotionHeal };
        var encounter = new CombatEncounter(character, MakeHarmlessMonster());
        int hpBefore = character.CurrentHealth;
        int logBefore = encounter.Log.Count;

        Assert.False(encounter.CharacterUseItem(notOwned));
        Assert.Equal(hpBefore, character.CurrentHealth);
        Assert.Equal(logBefore, encounter.Log.Count);
        Assert.Equal(CombatPhase.CharacterTurn, encounter.Phase);
    }

    // ── Group ─────────────────────────────────────────────────────────────

    [Fact]
    public void Group_UseItem_OnOwnTurn_HealsConsumesOne_AndAdvancesTheTurn()
    {
        var a = MakeWoundedCharacter("Alpha");
        var b = MakeWoundedCharacter("Beta");
        var encounter = new GroupCombatEncounter(new[] { a, b }, new[] { MakeHarmlessMonster() });

        var actor = encounter.Characters.First(c => c.Name == encounter.CurrentTurnCharacterName);
        var potion = GivePotions(actor, 3);
        int hpBefore = actor.CurrentHealth;

        Assert.True(encounter.CharacterUseItem(actor.Name, potion));

        Assert.Equal(hpBefore + PotionHeal, actor.CurrentHealth); // nobody else acts before the heal lands
        Assert.Equal(2, potion.StackSize);
        Assert.Contains(encounter.Log, e => e.Key == "pg.fight.log.usedItem");
        Assert.NotEqual(actor.Name, encounter.CurrentTurnCharacterName);
    }

    [Fact]
    public void Group_UseItem_NotYourTurn_IsRejected()
    {
        var a = MakeWoundedCharacter("Alpha");
        var b = MakeWoundedCharacter("Beta");
        var encounter = new GroupCombatEncounter(new[] { a, b }, new[] { MakeHarmlessMonster() });

        var waiting = encounter.Characters.First(c => c.Name != encounter.CurrentTurnCharacterName);
        var potion = GivePotions(waiting, 1);

        Assert.False(encounter.CharacterUseItem(waiting.Name, potion));
        Assert.Equal(1, potion.StackSize);
    }

    [Fact]
    public void Group_UseItem_FromAnotherCharactersInventory_IsRejectedWithoutSpendingTheTurn()
    {
        var a = MakeWoundedCharacter("Alpha");
        var b = MakeWoundedCharacter("Beta");
        var encounter = new GroupCombatEncounter(new[] { a, b }, new[] { MakeHarmlessMonster() });

        var actor = encounter.Characters.First(c => c.Name == encounter.CurrentTurnCharacterName);
        var other = encounter.Characters.First(c => c != actor);
        var othersPotion = GivePotions(other, 1);

        Assert.False(encounter.CharacterUseItem(actor.Name, othersPotion));
        Assert.Equal(1, othersPotion.StackSize);
        Assert.Equal(actor.Name, encounter.CurrentTurnCharacterName);
    }
}
