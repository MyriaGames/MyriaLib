using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Entities.NPCs
{
    public class Npc
    {
        public string Id { get; set; } = "";
        public string NameKey { get; set; } = "";        // e.g. "game.npc.healer.name"
        public string DescriptionKey { get; set; } = ""; // e.g. "game.npc.healer.desc"

        public NpcType Type { get; set; }
        public List<string> Services { get; set; } = new();
        public List<string> ItemNames { get; set; } = new();
        public List<Item> ItemRefs { get; set; } = new();
        public string? MasterJobId { get; set; }         // job this NPC teaches ("blacksmith", "tailor", …)
        public string? UpgradeCategory { get; set; }     // filters what items appear in the upgrade panel
        public override string ToString()
        {
            return Localization.T(NameKey);
        }
        // --- Healing (Healer) ---
        public NpcActionResult HealingAction(Character character)
        {
            // Use your new HP/MP event-friendly methods
            character.Heal(int.MaxValue, ToString());
            character.RestoreMana(int.MaxValue, ToString());

            return NpcActionResult.Ok("npc.action.heal.ok");
        }
        // --- Buy (Smith/Trader/Healer potions etc.) ---
        public NpcActionResult BuyItem(Character character, Item item, int amount = 1)
        {
            if (amount <= 0) return NpcActionResult.Fail("npc.action.amount.invalid");
            if (item == null) return NpcActionResult.Fail("npc.action.item.null");

            int totalCost = item.BuyPrice * amount;

            if (!character.Money.TrySpend(totalCost))
                return NpcActionResult.Fail("npc.action.buy.notEnoughMoney", totalCost);

            var toAdd = ItemFactory.CreateItem(item.Id, amount);
            if (toAdd == null)
                return NpcActionResult.Fail("npc.action.buy.itemNotFound", item.Id);

            // Inventory expansions are applied immediately and never stored as items
            if (toAdd is InventoryExpansion expansion)
            {
                for (int i = 0; i < amount; i++)
                    expansion.Use(character);
                return NpcActionResult.Ok("npc.action.buy.expansion.ok", character.Inventory.Pages);
            }

            bool ok = character.Inventory.AddItem(toAdd, character);
            if (!ok)
            {
                character.Money.TryAdd(totalCost);
                return NpcActionResult.Fail("npc.action.buy.inventoryFull");
            }

            return NpcActionResult.Ok("npc.action.buy.ok", amount, item.Id);
        }

        // --- Sell (Trader/Healer) ---
        public NpcActionResult SellItem(Character character, Item item, int amount = 1)
        {
            if (amount <= 0) return NpcActionResult.Fail("npc.action.amount.invalid");
            if (item == null) return NpcActionResult.Fail("npc.action.item.null");

            // The exact piece handed in if it's in the bag (equipment doesn't stack, so two items
            // with the same id can differ in upgrade level), else the first one with that id.
            var invItem = character.Inventory.Items.FirstOrDefault(i => ReferenceEquals(i, item))
                          ?? character.Inventory.Items.FirstOrDefault(i => i.Id == item.Id);
            if (invItem == null)
                return NpcActionResult.Fail("npc.action.sell.notOwned", item.Id);

            if (invItem.StackSize < amount)
                return NpcActionResult.Fail("npc.action.sell.notEnoughAmount", amount);

            int totalGain = JobManager.GetSellValue(invItem, character) * amount;

            // Check the purse first: the item used to be taken and the payment silently dropped when
            // the purse was full (same check as the realm server's SellItemToNpc).
            if (!character.Money.CanHold(totalGain))
                return NpcActionResult.Fail("npc.action.sell.moneyFull");

            // reduce stack / remove item
            if (invItem.StackSize == amount)
                character.Inventory.RemoveItem(invItem);
            else
                invItem.StackSize -= amount;

            character.Money.TryAdd(totalGain);

            return NpcActionResult.Ok("npc.action.sell.ok", amount, item.Id, totalGain);
        }
        // --- Upgrade (Smith) ---
        public NpcActionResult UpgradeItem(Character character, Item item, int maxUpgradeLevel = 10)
        {
            if (item is not EquipmentItem eq)
                return NpcActionResult.Fail("npc.action.upgrade.notEquipment");

            if (eq.TryUpgrade(character, maxUpgradeLevel))
                return NpcActionResult.Ok("npc.action.upgrade.ok", eq.Id, eq.UpgradeLevel);

            return NpcActionResult.Fail("npc.action.upgrade.fail", eq.Id);
        }
        // --- Craft (Smith) ---
        public NpcActionResult CraftItem(Character character, Item item)
        {

            // Crafting rules differ per game. The console version crafts upgrade_stone from iron_ore :contentReference[oaicite:7]{index=7}.
            // Keep this as a simple hook for now:
            return NpcActionResult.Fail("npc.action.craft.notImplemented");
        }

    }

}
