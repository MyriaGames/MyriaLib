using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Items;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Security review 2026-11-26, A06 F2-F4: money can't be "spent" negatively, and adding items is
/// all-or-nothing with stacks never larger than MaxStackSize.
/// </summary>
public class EconomyIntegrityTests
{
    // ── MoneyBag ──────────────────────────────────────────────────────────

    [Fact]
    public void TrySpend_NegativeAmount_IsRefused_AndDoesNotCredit()
    {
        var bag = new MoneyBag();
        bag.TryAdd(100);

        Assert.False(bag.TrySpend(-9_000_000_000_000_000_000));
        Assert.Equal(100, bag.Balance.BronzeTotal);
    }

    [Fact]
    public void TryAdd_NegativeAmount_IsRefused()
    {
        var bag = new MoneyBag();
        bag.TryAdd(100);

        Assert.False(bag.TryAdd(-50));
        Assert.Equal(100, bag.Balance.BronzeTotal);
    }

    [Fact]
    public void CanHold_HugeAmount_DoesNotOverflowIntoYes()
    {
        var bag = new MoneyBag { Capacity = 300_000 };
        bag.TryAdd(1);
        Assert.False(bag.CanHold(long.MaxValue)); // 1 + long.MaxValue used to wrap negative -> "fits"
        Assert.False(bag.TryAdd(long.MaxValue));
    }

    // ── Inventory ─────────────────────────────────────────────────────────

    private static Character CharacterWithFullInventory(MaterialItem keep)
    {
        var c = TestHelpers.CreateCharacter();
        c.Inventory.Items.Add(keep);
        for (int i = 1; c.Inventory.Items.Count < c.Inventory.Capacity; i++)
            c.Inventory.Items.Add(new MaterialItem { Id = "filler_" + i, Name = "Filler", StackSize = 1 });
        return c;
    }

    [Fact]
    public void AddItem_PartlyFitting_AddsNothing()
    {
        var potion = new MaterialItem { Id = "herb", Name = "Herb", StackSize = 1 }; // MaxStackSize 99
        var c = CharacterWithFullInventory(potion);

        var incoming = new MaterialItem { Id = "herb", Name = "Herb", StackSize = 100 }; // 98 would fit
        Assert.False(c.Inventory.AddItem(incoming, c));

        Assert.Equal(1, potion.StackSize);              // the existing stack was NOT topped up
        Assert.Equal(100, incoming.StackSize);
    }

    [Fact]
    public void AddItem_FittingIntoExistingStack_WorksWithAFullInventory()
    {
        var potion = new MaterialItem { Id = "herb", Name = "Herb", StackSize = 1 };
        var c = CharacterWithFullInventory(potion);

        Assert.True(c.Inventory.AddItem(new MaterialItem { Id = "herb", Name = "Herb", StackSize = 98 }, c));
        Assert.Equal(99, potion.StackSize);
    }

    [Fact]
    public void AddItem_MoreThanMaxStack_IsSplitIntoProperStacks()
    {
        var c = TestHelpers.CreateCharacter();
        var incoming = new MaterialItem { Id = "ore", Name = "Ore", StackSize = 250 }; // MaxStackSize 99

        Assert.True(c.Inventory.AddItem(incoming, c));

        var stacks = c.Inventory.Items.Where(i => i.Id == "ore").ToList();
        Assert.Equal(250, stacks.Sum(s => s.StackSize));
        Assert.All(stacks, s => Assert.True(s.StackSize <= s.MaxStackSize));
        Assert.Equal(3, stacks.Count);
    }

    [Fact]
    public void AddItem_HugeQuantity_IsRefused()
    {
        var c = TestHelpers.CreateCharacter();
        Assert.False(c.Inventory.AddItem(new MaterialItem { Id = "ore", Name = "Ore", StackSize = 1_073_741_824 }, c));
        Assert.DoesNotContain(c.Inventory.Items, i => i.Id == "ore");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AddItem_NonPositiveStack_IsRefused(int stack)
    {
        var c = TestHelpers.CreateCharacter();
        Assert.False(c.Inventory.AddItem(new MaterialItem { Id = "ore", Name = "Ore", StackSize = stack }, c));
    }
}
