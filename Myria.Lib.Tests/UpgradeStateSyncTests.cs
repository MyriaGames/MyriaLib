using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Equipment's upgrade level and craft quality survive every load/sync path: restoring from saved
/// values gives the same bonuses as upgrading in play, and the server's inventory/equipment pushes
/// keep the right piece (they used to identify items by id only).
/// </summary>
[Collection("GameData")]
public class UpgradeStateSyncTests
{
    public UpgradeStateSyncTests(GameDataFixture _) { }

    private static EquipmentItem Upgraded(string id, int level, float quality = 1f)
    {
        var item = (EquipmentItem)ItemFactory.CreateItem(id)!;
        item.CraftQuality = quality;
        for (int i = 0; i < level; i++) item.TryUpgrade_Internal();
        return item;
    }

    [Fact]
    public void RestoringAnUpgradeState_GivesTheSameBonusesAsUpgradingInPlay()
    {
        var played = Upgraded("iron_sword", 4, 1.15f);
        var restored = (EquipmentItem)ItemFactory.CreateItem("iron_sword", 1, 4, 1.15f)!;

        Assert.Equal(4, restored.UpgradeLevel);
        Assert.Equal(played.BonusATK, restored.BonusATK);
        Assert.Equal(played.BonusAim, restored.BonusAim);
    }

    [Fact]
    public void AnInventoryPush_RemovesTheRightPiece_AndBringsBackUpgradedOnes()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Clear();
        character.Inventory.Items.Add(Upgraded("iron_sword", 0));
        character.Inventory.Items.Add(Upgraded("iron_sword", 3));

        // The server sold the +0 one: only the +3 remains.
        character.Inventory.ApplySnapshot(new[] { new InventoryItemSnapshot("iron_sword", 1, 3, 1f) }, character);
        Assert.Equal(3, Assert.Single(character.Inventory.Items.OfType<EquipmentItem>()).UpgradeLevel);

        // An upgraded piece the client doesn't have comes back upgraded, not as a plain copy.
        character.Inventory.ApplySnapshot(new[] { new InventoryItemSnapshot("iron_sword", 1, 3, 1f), new InventoryItemSnapshot("steel_sword", 1, 2, 1.1f) }, character);
        var steel = character.Inventory.Items.OfType<EquipmentItem>().Single(i => i.Id == "steel_sword");
        Assert.Equal(2, steel.UpgradeLevel);
        Assert.Equal(1.1f, steel.CraftQuality, 3);
    }

    [Fact]
    public void AnEquipmentPush_KeepsTheEquippedPiecesUpgrade()
    {
        var character = TestHelpers.CreateCharacter();
        character.ApplySyncedEquipment(new EquippedSnapshot("iron_sword", null, null, WeaponUpgradeLevel: 3, WeaponCraftQuality: 1.2f));

        Assert.Equal(3, character.WeaponSlot!.UpgradeLevel);
        Assert.Equal(Upgraded("iron_sword", 3, 1.2f).BonusATK, character.WeaponSlot.BonusATK);
        Assert.Null(character.ArmorSlot);
    }
}
