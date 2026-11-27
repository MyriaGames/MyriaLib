using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Healing potions heal a percentage of max HP (v0.3): T1 5%, T2 7.5%, T3 10%. A flat amount
/// fell far behind HP growth - 25 HP was under 2% of a level-10 fighter.
/// </summary>
[Collection("GameData")]
public class PotionHealTests
{
    public PotionHealTests(GameDataFixture _) { }

    [Theory]
    [InlineData("t1_healing_potion", 5.0)]
    [InlineData("t2_healing_potion", 7.5)]
    [InlineData("t3_healing_potion", 10.0)]
    public void HealingPotions_AreDefinedAsPercentages(string id, double percent)
    {
        var potion = Assert.IsType<ConsumableItem>(ItemFactory.CreateItem(id));
        Assert.Equal(percent, potion.HealPercent);
    }

    [Fact]
    public void PercentPotion_HealsItsShareOfMaxHp_RoundedUp()
    {
        var character = TestHelpers.CreateCharacter(level: 10);
        int max = character.MaxHealth;
        character.CurrentHealth = 1;
        var potion = new ConsumableItem { Id = "p", Name = "p", Description = "", HealPercent = 7.5 };

        potion.Use(character);

        Assert.Equal(1 + (int)Math.Ceiling(max * 7.5 / 100.0), character.CurrentHealth);
    }

    [Fact]
    public void PercentWinsOverFlatAmount_AndFlatStillWorksAlone()
    {
        var character = TestHelpers.CreateCharacter(level: 10);
        var both = new ConsumableItem { Id = "a", Name = "a", Description = "", HealAmount = 1, HealPercent = 10 };
        var flat = new ConsumableItem { Id = "b", Name = "b", Description = "", HealAmount = 25 };

        Assert.Equal((int)Math.Ceiling(character.MaxHealth / 10.0), both.HealFor(character));
        Assert.Equal(25, flat.HealFor(character));
    }
}
