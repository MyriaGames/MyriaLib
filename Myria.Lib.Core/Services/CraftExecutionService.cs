using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;

namespace Myria.Lib.Core.Services
{
    /// <summary>
    /// Outcome of a single <see cref="CraftExecutionService.Craft"/> attempt. ItemId/Amount/JobId/
    /// XpGranted are only meaningful when Success is true.
    /// </summary>
    public readonly record struct CraftOutcome(
        bool Success,
        string? Reason, // null on success; "unknown_recipe" | "knowledge_required" | "missing_ingredients" | "item_not_found" | "inventory_full"
        string? ItemId,
        int Amount,
        string? JobId,
        long XpGranted);

    /// <summary>
    /// Outcome of a single <see cref="CraftExecutionService.Upgrade"/> attempt.
    /// </summary>
    public readonly record struct UpgradeOutcome(
        bool Success,
        string? Reason, // null on success; "knowledge_required" | "missing_materials" | "upgrade_failed"
        string ItemId,
        int UpgradeLevel,
        string? JobId,
        long XpGranted);

    /// <summary>
    /// Executes the crafting and equipment-upgrade actions at an NPC. Deliberately separate from
    /// <see cref="CraftingService"/> (which only looks up recipe data and has no dependency on
    /// Character/Item at all) so that lookup stays engine-agnostic while this — which necessarily
    /// depends on Character, Inventory, and job progression — carries the coupling instead.
    /// <para>
    /// Both single-player (CraftPanelViewModel/UpgradePanelViewModel) and multiplayer (GameHub)
    /// used to have their own independent, hand-duplicated copies of this logic, which had each
    /// drifted from the other in different ways (see method docs below for specifics).
    /// </para>
    /// </summary>
    public static class CraftExecutionService
    {
        /// <summary>
        /// Crafts <paramref name="quantity"/> of the recipe that produces <paramref name="outputItemId"/>
        /// at <paramref name="npc"/>, validating and consuming ingredients. Refunds already-consumed
        /// ingredients if the output item can't be created or the inventory has no room for it —
        /// the single-player path used to skip the former refund, and the multiplayer path used to
        /// skip the latter (and granted XP even when the crafted item silently failed to be added).
        /// </summary>
        public static CraftOutcome Craft(Character character, Npc npc, string outputItemId, int quantity)
        {
            if (quantity <= 0)
                return new CraftOutcome(false, "invalid_quantity", null, 0, null, 0);

            var recipe = CraftingService.GetRecipe(npc.Id, outputItemId);
            if (recipe == null)
                return new CraftOutcome(false, "unknown_recipe", null, 0, null, 0);

            string? jobId = npc.MasterJobId;
            if (!string.IsNullOrEmpty(jobId))
            {
                int knowledge = JobXpService.GetLevel(JobManager.GetOrAdd(character, jobId).KnowledgeXp);
                if (knowledge < recipe.RequiredKnowledgeLevel)
                    return new CraftOutcome(false, "knowledge_required", null, 0, null, 0);
            }

            foreach (var ing in recipe.Ingredients)
            {
                int owned = character.Inventory.Items.Where(i => i.Id == ing.ItemId).Sum(i => i.StackSize);
                if (owned < ing.Amount * quantity)
                    return new CraftOutcome(false, "missing_ingredients", null, 0, null, 0);
            }

            foreach (var ing in recipe.Ingredients)
            {
                int remaining = ing.Amount * quantity;
                foreach (var stack in character.Inventory.Items.Where(i => i.Id == ing.ItemId).ToList())
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(stack.StackSize, remaining);
                    stack.StackSize -= take;
                    remaining -= take;
                    if (stack.StackSize <= 0) character.Inventory.RemoveItem(stack);
                }
            }

            void RefundIngredients()
            {
                foreach (var ing in recipe.Ingredients)
                {
                    if (ItemFactory.TryCreateItem(ing.ItemId, out var refund))
                    {
                        refund.StackSize = ing.Amount * quantity;
                        character.Inventory.AddItem(refund, character);
                    }
                }
            }

            if (!ItemFactory.TryCreateItem(recipe.OutputId, out var output))
            {
                RefundIngredients();
                return new CraftOutcome(false, "item_not_found", null, 0, null, 0);
            }

            if (output is EquipmentItem crafted && !string.IsNullOrEmpty(jobId))
            {
                crafted.CraftQuality = (float)JobXpService.GetCraftQualityFromXp(JobManager.GetOrAdd(character, jobId).SkillXp);
                crafted.Bonuses = crafted.BaseStats.Scale(crafted.CraftQuality);
            }

            output.StackSize = quantity;
            if (!character.Inventory.AddItem(output, character))
            {
                RefundIngredients();
                return new CraftOutcome(false, "inventory_full", null, 0, null, 0);
            }

            long xpGranted = 0;
            if (!string.IsNullOrEmpty(jobId))
            {
                xpGranted = recipe.XpReward * quantity;
                JobManager.GrantSkillXp(character, jobId, xpGranted);
            }

            return new CraftOutcome(true, null, recipe.OutputId, quantity, jobId, xpGranted);
        }

        /// <summary>
        /// Upgrades <paramref name="item"/> (already resolved/selected by the caller) at
        /// <paramref name="npc"/>, validating the knowledge gate before consuming any materials
        /// (the single-player path used to skip this pre-check, relying entirely on its UI button
        /// being disabled, which meant nothing re-verified it at execution time).
        /// </summary>
        public static UpgradeOutcome Upgrade(Character character, Npc npc, EquipmentItem item)
        {
            string matId = CraftingService.UpgradeMaterialFor(npc.UpgradeCategory);
            int matCount = CraftingService.UpgradeMaterialCount(item.UpgradeLevel);

            string? jobId = npc.MasterJobId;
            int knowledgeMax = 10;
            if (!string.IsNullOrEmpty(jobId))
            {
                int knowledgeLevel = JobXpService.GetLevel(JobManager.GetOrAdd(character, jobId).KnowledgeXp);
                knowledgeMax = JobXpService.GetMaxUpgradeLevel(knowledgeLevel);
            }
            if (item.UpgradeLevel >= knowledgeMax)
                return new UpgradeOutcome(false, "knowledge_required", item.Id, item.UpgradeLevel, jobId, 0);

            int have = character.Inventory.Items.Where(i => i.Id == matId).Sum(i => i.StackSize);
            if (have < matCount)
                return new UpgradeOutcome(false, "missing_materials", item.Id, item.UpgradeLevel, jobId, 0);

            int remaining = matCount;
            foreach (var stack in character.Inventory.Items.Where(i => i.Id == matId).ToList())
            {
                if (remaining <= 0) break;
                int take = Math.Min(stack.StackSize, remaining);
                stack.StackSize -= take;
                remaining -= take;
                if (stack.StackSize <= 0) character.Inventory.RemoveItem(stack);
            }

            if (!string.IsNullOrEmpty(jobId))
            {
                float quality = (float)JobXpService.GetCraftQualityFromXp(JobManager.GetOrAdd(character, jobId).SkillXp);
                if (quality > item.CraftQuality) item.CraftQuality = quality;
            }

            if (!item.TryUpgrade(character, knowledgeMax))
                return new UpgradeOutcome(false, "upgrade_failed", item.Id, item.UpgradeLevel, jobId, 0);

            long xpGranted = 0;
            if (!string.IsNullOrEmpty(jobId))
            {
                xpGranted = 25;
                JobManager.GrantSkillXp(character, jobId, xpGranted);
            }

            return new UpgradeOutcome(true, null, item.Id, item.UpgradeLevel, jobId, xpGranted);
        }
    }
}
