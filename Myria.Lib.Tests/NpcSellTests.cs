using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Npc.SellItem (the Console's offline sell) took the item and then silently dropped the payment
/// when the purse was full (MONO-002). It also always sold the first item with that id, which with
/// non-stacking equipment can be the wrong (upgraded) piece.
/// </summary>
[Collection("GameData")]
public class NpcSellTests
{
    public NpcSellTests(GameDataFixture _) { }

    private static Npc AnyNpc() => NpcService.TryGet("smith_default", out var npc) ? npc! : throw new InvalidOperationException();

    [Fact]
    public void SellingIntoAFullPurse_KeepsTheItem_AndFails()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Clear();
        var ore = ItemFactory.CreateItem("iron_ore", 5)!;
        character.Inventory.Items.Add(ore);
        character.Money.Capacity = character.Money.Balance.BronzeTotal;   // full

        var result = AnyNpc().SellItem(character, ore, 5);

        Assert.False(result.Success);
        Assert.Equal("npc.action.sell.moneyFull", result.MessageKey);
        Assert.Equal(5, character.Inventory.Items.Single(i => i.Id == "iron_ore").StackSize);
    }

    [Fact]
    public void SellingAPiece_SellsThatExactPiece()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Clear();
        var plain = (EquipmentItem)ItemFactory.CreateItem("iron_sword")!;
        var upgraded = (EquipmentItem)ItemFactory.CreateItem("iron_sword", 1, 3, 1f)!;
        character.Inventory.Items.Add(upgraded);
        character.Inventory.Items.Add(plain);

        Assert.True(AnyNpc().SellItem(character, plain).Success);

        Assert.Same(upgraded, Assert.Single(character.Inventory.Items));
    }
}
