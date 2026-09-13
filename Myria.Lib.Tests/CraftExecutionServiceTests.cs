using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Services;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// CraftExecutionService.Craft/Upgrade consolidate what used to be three independent copies of
/// this logic (MyriaRPG's CraftPanelViewModel/UpgradePanelViewModel and MyriaServer's GameHub),
/// which had each drifted from the others in different ways — see the service's doc comments for
/// specifics. Tests use the real "smith_default" recipe/NPC data (recipes.json/npcs.json) rather
/// than injecting synthetic recipes, since CraftingService has no public accessor to snapshot and
/// restore its loaded state (same constraint as RuneWordService — see RuneEvaluatorTests). Needs
/// [Collection("GameData")] for ItemFactory and the real recipes.json content.
/// </summary>
[Collection("GameData")]
public class CraftExecutionServiceTests
{
    public CraftExecutionServiceTests(GameDataFixture _) { }

    // smith_default: masterJobId=blacksmith, upgradeCategory=blacksmith.
    // recipes.json: smith_default crafts "iron_ingot" from 2x "iron_ore", RequiredKnowledgeLevel=1.
    private static readonly Npc Smith = new() { Id = "smith_default", MasterJobId = "blacksmith", UpgradeCategory = "blacksmith" };

    [Fact]
    public void Craft_UnknownRecipe_ReturnsUnknownRecipe()
    {
        var character = TestHelpers.CreateCharacter();
        var outcome = CraftExecutionService.Craft(character, Smith, "not_a_real_recipe", 1);

        Assert.False(outcome.Success);
        Assert.Equal("unknown_recipe", outcome.Reason);
    }

    [Fact]
    public void Craft_MissingIngredients_ReturnsMissingIngredients_AndConsumesNothing()
    {
        var character = TestHelpers.CreateCharacter();
        var outcome = CraftExecutionService.Craft(character, Smith, "iron_ingot", 1);

        Assert.False(outcome.Success);
        Assert.Equal("missing_ingredients", outcome.Reason);
        Assert.Empty(character.Inventory.Items);
    }

    [Fact]
    public void Craft_Success_ConsumesIngredientsAddsOutputAndGrantsSkillXp()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Add(new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 2 });

        var outcome = CraftExecutionService.Craft(character, Smith, "iron_ingot", 1);

        Assert.True(outcome.Success);
        Assert.Equal("iron_ingot", outcome.ItemId);
        Assert.Equal(1, outcome.Amount);
        Assert.Equal("blacksmith", outcome.JobId);
        Assert.Equal(5, outcome.XpGranted); // recipe's XpReward
        Assert.DoesNotContain(character.Inventory.Items, i => i.Id == "iron_ore"); // fully consumed
        Assert.Contains(character.Inventory.Items, i => i.Id == "iron_ingot");
    }

    [Fact]
    public void Craft_PartialIngredientStack_ConsumesOnlyWhatsNeeded()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Add(new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 10 });

        var outcome = CraftExecutionService.Craft(character, Smith, "iron_ingot", 1);

        Assert.True(outcome.Success);
        var remainingOre = Assert.Single(character.Inventory.Items, i => i.Id == "iron_ore");
        Assert.Equal(8, remainingOre.StackSize); // 10 - 2 consumed
    }

    [Fact]
    public void Craft_InventoryFull_RefundsConsumedIngredients()
    {
        int originalPageSize = Inventory.PageSize;
        try
        {
            Inventory.PageSize = 2; // exactly 2 slots
            var character = TestHelpers.CreateCharacter();
            // 10 ore (not fully consumed by a single craft, so its slot stays occupied) + one
            // unrelated filler item -> both slots stay full after ingredient consumption, so
            // there's no room left for the crafted output.
            character.Inventory.Items.Add(new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 10 });
            character.Inventory.Items.Add(new MaterialItem { Id = "stone_fragment", Name = "Stone", StackSize = 1 });

            var outcome = CraftExecutionService.Craft(character, Smith, "iron_ingot", 1);

            Assert.False(outcome.Success);
            Assert.Equal("inventory_full", outcome.Reason);
            var ore = Assert.Single(character.Inventory.Items, i => i.Id == "iron_ore");
            Assert.Equal(10, ore.StackSize); // refunded back to the original amount
        }
        finally
        {
            Inventory.PageSize = originalPageSize;
        }
    }

    [Fact]
    public void Upgrade_Success_ConsumesMaterialAndRaisesUpgradeLevel()
    {
        var character = TestHelpers.CreateCharacter();
        var sword = new EquipmentItem { Id = "sword", Name = "Sword", UpgradeLevel = 0 };
        character.Inventory.Items.Add(new MaterialItem { Id = "iron_ingot", Name = "Iron Ingot", StackSize = 1 });

        var outcome = CraftExecutionService.Upgrade(character, Smith, sword);

        Assert.True(outcome.Success);
        Assert.Equal(1, sword.UpgradeLevel);
        Assert.Equal("blacksmith", outcome.JobId);
        Assert.Equal(25, outcome.XpGranted);
        Assert.DoesNotContain(character.Inventory.Items, i => i.Id == "iron_ingot"); // consumed
    }

    [Fact]
    public void Upgrade_MissingMaterials_ReturnsMissingMaterials_AndLeavesItemUnchanged()
    {
        var character = TestHelpers.CreateCharacter();
        var sword = new EquipmentItem { Id = "sword", Name = "Sword", UpgradeLevel = 0 };

        var outcome = CraftExecutionService.Upgrade(character, Smith, sword);

        Assert.False(outcome.Success);
        Assert.Equal("missing_materials", outcome.Reason);
        Assert.Equal(0, sword.UpgradeLevel);
    }

    [Fact]
    public void Upgrade_AtKnowledgeCap_ReturnsKnowledgeRequired_WithoutConsumingMaterials()
    {
        // A fresh character's blacksmith knowledge level defaults to JobXpService.DefaultMaxUpgrade
        // (2) — an item already at that upgrade level can't go further until knowledge increases.
        var character = TestHelpers.CreateCharacter();
        var sword = new EquipmentItem { Id = "sword", Name = "Sword", UpgradeLevel = 2 };
        character.Inventory.Items.Add(new MaterialItem { Id = "iron_ingot", Name = "Iron Ingot", StackSize = 5 });

        var outcome = CraftExecutionService.Upgrade(character, Smith, sword);

        Assert.False(outcome.Success);
        Assert.Equal("knowledge_required", outcome.Reason);
        Assert.Equal(2, sword.UpgradeLevel);
        Assert.Equal(5, character.Inventory.Items.Single(i => i.Id == "iron_ingot").StackSize); // untouched
    }
}
