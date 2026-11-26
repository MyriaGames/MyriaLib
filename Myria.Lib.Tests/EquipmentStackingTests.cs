using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Equipment used to stack to 50 (BuildEquipment never set MaxStackSize, so it fell back to Item's
/// default) and stacking only compared ids - a +3 iron sword added next to a +0 one became one stack
/// of 2 and the upgrade was gone. Reported from the Mono client work.
/// </summary>
[Collection("GameData")]
public class EquipmentStackingTests
{
    public EquipmentStackingTests(GameDataFixture _) { }

    private static EquipmentItem Sword(int upgradeLevel = 0)
    {
        var sword = (EquipmentItem)ItemFactory.CreateItem("iron_sword")!;
        sword.UpgradeLevel = upgradeLevel;
        return sword;
    }

    [Fact]
    public void EquipmentFromTheFactory_DoesNotStack()
    {
        Assert.Equal(1, Sword().MaxStackSize);
        Assert.Equal(1, ItemFactory.CreateItem("pickaxe")!.MaxStackSize);   // tools are equipment too
    }

    [Fact]
    public void AnUpgradedPiece_StaysItsOwnItem()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Clear();
        Assert.True(character.Inventory.AddItem(Sword(0), character));
        Assert.True(character.Inventory.AddItem(Sword(3), character));

        var swords = character.Inventory.Items.OfType<EquipmentItem>().Where(i => i.Id == "iron_sword").ToList();
        Assert.Equal(2, swords.Count);
        Assert.Contains(swords, s => s.UpgradeLevel == 3);
        Assert.All(swords, s => Assert.Equal(1, s.StackSize));
    }

    [Fact]
    public void EvenWithALargerStackSize_OnlyIdenticalPiecesStack()
    {
        var plain = Sword(0);
        var upgraded = Sword(3);
        plain.MaxStackSize = upgraded.MaxStackSize = 10;    // e.g. mod data

        Assert.False(plain.CanStackWith(upgraded));
        Assert.True(plain.CanStackWith(Sword(0)));
    }

    [Fact]
    public void BuyingSeveralPieces_GivesSeparateItems()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Clear();
        var bought = ItemFactory.CreateItem("iron_sword")!;
        bought.StackSize = 3;

        Assert.True(character.Inventory.AddItem(bought, character));

        Assert.Equal(3, character.Inventory.Items.Count(i => i.Id == "iron_sword"));
    }

    [Fact]
    public void AMergedStackFromAnOldSave_IsSplitBackIntoSingleItems()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Clear();
        var merged = Sword(2);
        merged.StackSize = 3;                                // as an old save stored it
        character.Inventory.Items.Add(merged);
        character.Inventory.Items.Add(ItemFactory.CreateItem("iron_ore", 20)!);

        Assert.Equal(2, character.Inventory.SplitOverfullStacks());

        var swords = character.Inventory.Items.OfType<EquipmentItem>().ToList();
        Assert.Equal(3, swords.Count);
        Assert.All(swords, s => { Assert.Equal(1, s.StackSize); Assert.Equal(2, s.UpgradeLevel); });
        Assert.Equal(20, character.Inventory.Items.Single(i => i.Id == "iron_ore").StackSize); // materials untouched
        Assert.Equal(0, character.Inventory.SplitOverfullStacks());
    }
}
