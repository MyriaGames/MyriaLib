using Myria.Lib.Core.Utils;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Events;

namespace Myria.Lib.Core.Entities.Characters
{
    public class Inventory
    {
        public event EventHandler<ItemReceivedEventArgs>? ItemReceived;
        public event EventHandler<ItemReceivedEventArgs>? ItemRemoved;
        public event EventHandler<ItemReceivedEventArgs>? ItemSold;

        /// <summary>Slot count per inventory page. Defaults to 49 (7×7 UI grid); override via <see cref="Myria.Lib.Core.Systems.GameConfig"/>.</summary>
        public static int PageSize { get; set; } = 49;

        /// <summary>Number of unlocked pages. Starts at 1; increased by purchasing inventory expansions.</summary>
        public int Pages { get; set; } = 1;

        /// <summary>Total slot capacity across all unlocked pages.</summary>
        public int Capacity => Pages * PageSize;

        public List<Item> Items { get; set; } = new();

        /// <summary>
        /// Returns a fixed-length array of <see cref="PageSize"/> slots for the given page (0-indexed).
        /// Empty slots are null. Suitable for binding directly to a 7×7 UI grid.
        /// </summary>
        public Item?[] GetPage(int page)
        {
            var result = new Item?[PageSize];
            int start = page * PageSize;
            for (int i = 0; i < PageSize; i++)
            {
                int idx = start + i;
                result[i] = idx < Items.Count ? Items[idx] : null;
            }
            return result;
        }

        /// <summary>
        /// Returns only the non-null items on the given page. Useful for logic that
        /// doesn't need slot positions (console pager, quest tracking, etc.).
        /// </summary>
        public IEnumerable<Item> GetItemsOnPage(int page) =>
            Items.Skip(page * PageSize).Take(PageSize);

        /// <summary>How many pages currently contain at least one item (always ≥ 1).</summary>
        public int UsedPages => Math.Max(1, (int)Math.Ceiling((double)Items.Count / PageSize));

        public bool SwapEquipment(string itemId, Character character) =>
            SwapEquipment(itemId, character, out _);

        /// <summary>Same as <see cref="SwapEquipment(string, Character)"/>, but reports why it failed
        /// (used by the multiplayer server so it can tell the client what went wrong instead of
        /// silently doing nothing).</summary>
        public bool SwapEquipment(string itemId, Character character, out string? reason)
        {
            var match = InventoryUtils.ResolveInventoryItem(itemId, character);
            if (match is not EquipmentItem equipment)
            {
                reason = "Item not found in inventory.";
                return false;
            }
            if (!equipment.IsUsableBy(character))
            {
                reason = "Your class cannot equip this item.";
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"[Inventory] Swapping {equipment.SlotType} slot: {equipment.Name}");

            var previous = character.Equipped.GetValueOrDefault(equipment.SlotType);
            character.Equipped[equipment.SlotType] = equipment;
            RemoveItem(equipment);
            if (previous != null) AddItem(previous, character);
            reason = null;
            return true;
        }
        public bool UnequipItem(string itemname, Character character)
        {
            System.Diagnostics.Debug.WriteLine($"[Inventory] UnequipItem called for: {itemname}");
            
            var match = InventoryUtils.ResolveInventoryItem(itemname, character);
            System.Diagnostics.Debug.WriteLine($"[Inventory] Resolved item: {match?.Name ?? "NULL"}");
            
            if (match is not EquipmentItem equipment)
            {
                System.Diagnostics.Debug.WriteLine($"[Inventory] Item is not EquipmentItem");
                return false;
            }
            
            if (equipment.IsUsableBy(character))
            {
                System.Diagnostics.Debug.WriteLine($"[Inventory] Equipment IS usable by player (should be false for this check)");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"[Inventory] Equipment found: {equipment.Name}, SlotType: {equipment.SlotType}");
            
            switch (equipment.SlotType)
            {
                case EquipmentType.Weapon:
                    {
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Unequipping from Weapon slot");
                        
                        if (character.WeaponSlot == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Inventory] Weapon slot is empty");
                            return false;
                        }
                        
                        EquipmentItem we = character.WeaponSlot;
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Adding unequipped weapon to inventory: {we.Name}");
                        
                        if (AddItem(we, character))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Inventory] Item added successfully, clearing weapon slot");
                            character.WeaponSlot = null;
                            return true;
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Failed to add item to inventory (inventory full?)");
                        return false;
                    }
                case EquipmentType.Armor:
                    {
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Unequipping from Armor slot");
                        
                        if (character.ArmorSlot == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Inventory] Armor slot is empty");
                            return false;
                        }
                        
                        EquipmentItem arm = character.ArmorSlot;
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Adding unequipped armor to inventory: {arm.Name}");
                        
                        if (AddItem(arm, character))
                        { 
                            System.Diagnostics.Debug.WriteLine($"[Inventory] Item added successfully, clearing armor slot");
                            character.ArmorSlot = null;
                            return true;
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Failed to add item to inventory (inventory full?)");
                        return false;
                    }
                case EquipmentType.Accessory:
                    {
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Unequipping from Accessory slot");
                        
                        if (character.AccessorySlot == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Inventory] Accessory slot is empty");
                            return false;
                        }
                        
                        EquipmentItem accs = character.AccessorySlot;
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Adding unequipped accessory to inventory: {accs.Name}");
                        
                        if (AddItem(accs, character))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Inventory] Item added successfully, clearing accessory slot");
                            character.AccessorySlot = null;
                            return true;
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[Inventory] Failed to add item to inventory (inventory full?)");
                        return false;
                    }
            }
            
            System.Diagnostics.Debug.WriteLine($"[Inventory] UnequipItem failed - unknown slot type");
            return false;
        }

        /// <summary>
        /// Unequips whatever is in <paramref name="slotType"/> and returns it to
        /// <paramref name="character"/>'s inventory. Returns false (no state changed) if the slot
        /// is already empty or the inventory has no room for the returned item.
        /// <para>
        /// This is the single implementation single-player (EquipmentViewModel.ExecuteUnequip) and
        /// multiplayer (GameHub.UnequipItem) both now call — previously each had its own identical
        /// by-slot-type logic hand-duplicated independently. Not to be confused with the older
        /// <see cref="UnequipItem(string, Character)"/> overload above, which resolves by item name
        /// against the loose inventory list — that can never actually match an equipped item (it
        /// isn't in that list) and is unrelated to this method.
        /// </para>
        /// </summary>
        public bool UnequipSlot(string slotType, Character character)
        {
            var item = character.Equipped.GetValueOrDefault(slotType);
            if (item == null) return false;

            if (!AddItem(item, character, "unequip"))
                return false;

            character.Equipped[slotType] = null;
            return true;
        }

        public bool UseItem(string itemname, Character character)
        {
            var item = InventoryUtils.ResolveInventoryItem(itemname, character);
            if (item == null || item is not ConsumableItem consumable) 
                return false;

            consumable.Use(character);
            ConsumeOne(item);
            return true;
        }

        /// <summary>
        /// Removes exactly one unit of <paramref name="item"/>: decrements its stack, or removes the
        /// entry entirely when it's the last one. Shared by every "use a consumable" path (inventory
        /// and combat) - <see cref="RemoveItem"/> drops the whole stack, which is wrong for using a
        /// single potion out of a stack of several.
        /// </summary>
        /// <returns>false if <paramref name="item"/> isn't an entry in this inventory.</returns>
        public bool ConsumeOne(Item item)
        {
            if (!Items.Contains(item)) return false;

            if (item.StackSize > 1)
            {
                item.StackSize--;
                Restack();
                ItemRemoved?.Invoke(this, new ItemReceivedEventArgs(item.CloneOne(), 1));
                return true;
            }

            return RemoveItem(item);
        }
        /// <summary>
        /// tries to add item to the inventory
        /// </summary>
        /// <param name="item">item to add</param>
        /// <param name="player">player character</param>
        /// <returns>if the item was added</returns>
        // All-or-nothing (security review 2026-11-26, A06 F3/F4): it used to top up existing stacks
        // first and only then report "full" without undoing that - callers that charge only after a
        // successful add (NPC shop) then handed out items for free - and it put whatever was left into
        // ONE new stack regardless of MaxStackSize (a crafted quantity of ~10^9 became one stack).
        // Now: nothing changes unless the whole amount fits, and new stacks respect MaxStackSize.
        public bool AddItem(Item item, Character character, string? source = null)
        {
            int stackSize = item.StackSize;
            if (stackSize <= 0 || !CanAdd(item, stackSize))
                return false;

            // First top up existing stacks
            foreach (var existing in Items.ToList())
            {
                if (item.StackSize == 0) break;
                if (existing.CanStackWith(item) && existing.StackSize < existing.MaxStackSize)
                {
                    int toAdd = Math.Min(existing.MaxStackSize - existing.StackSize, item.StackSize);
                    existing.StackSize += toAdd;
                    item.StackSize -= toAdd;
                }
            }

            // Then the rest as new stacks of at most MaxStackSize each (CanAdd guaranteed the slots)
            int max = Math.Max(1, item.MaxStackSize);
            while (item.StackSize > max)
            {
                var full = item.CloneOne();
                full.StackSize = max;
                item.StackSize -= max;
                Items.Add(full);
            }
            if (item.StackSize > 0)
                Items.Add(item);                        // the caller's object holds the last part

            Restack();
            GameEvents.FireItemReceived(character, item, stackSize);
            ItemReceived?.Invoke(this, new ItemReceivedEventArgs(item, stackSize, source));
            return true;
        }

        /// <summary>Whether <paramref name="quantity"/> of <paramref name="item"/> fits completely
        /// (free space in matching stacks plus free slots × MaxStackSize).</summary>
        public bool CanAdd(Item item, int quantity)
        {
            if (quantity <= 0) return false;
            long space = Items.Where(e => e.CanStackWith(item))
                              .Sum(e => (long)Math.Max(0, e.MaxStackSize - e.StackSize));
            space += (long)Math.Max(0, Capacity - Items.Count) * Math.Max(1, item.MaxStackSize);
            return space >= quantity;
        }
        /// <summary>
        /// removes an item from the inventory
        /// </summary>
        /// <param name="item">item to remove</param>
        /// <returns>if the item was removed</returns>
        public bool RemoveItem(Item item)
        {
            if (!Items.Remove(item)) return false;
            ItemRemoved?.Invoke(this, new ItemReceivedEventArgs(item, item.StackSize));
            return true;
        }

        /// <summary>
        /// tries to sell an item
        /// </summary>
        /// <param name="name">item name</param>
        /// <param name="quantity">amount to be sold</param>
        /// <param name="player">player character</param>
        /// <returns>if the item was successfully sold</returns>
        public bool SellItem(string name, int quantity, ref Character character)
        {
            var item = Items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item == null || quantity <= 0)
                return false;

            if (item.StackSize < quantity)
                return false;

            int coinsReceived = JobManager.GetSellValue(item, character) * quantity;

            // Snapshot item data before any mutation
            Item soldCopy = item.CloneOne();
            soldCopy.StackSize = quantity;

            // Reduce stack or remove item
            if (character.Money.TryAdd(coinsReceived))
            {
                if (item.StackSize == quantity)
                    Items.Remove(item);
                else
                    item.StackSize -= quantity;

                Restack();
                ItemSold?.Invoke(this, new ItemReceivedEventArgs(soldCopy, quantity, "sell"));
                return true;
            }
            else
                return false;
        }
        /// <summary>
        /// Reconciles this inventory against the server's authoritative item list (item id +
        /// total owned amount, no slot positions) - used by the generic CharacterUpdated sync
        /// push. Only items whose total owned quantity actually differs are touched, and the
        /// same ItemReceived/ItemRemoved events a local pickup/deposit would fire are fired here
        /// too (including on a partial-stack decrement, which RemoveItem alone doesn't cover),
        /// so existing UI listeners (e.g. InventoryGridViewModel) refresh exactly as if the
        /// change had happened locally - no explicit RefreshInventory() call needed by callers.
        /// </summary>
        public void ApplySnapshot(IReadOnlyList<InventoryItemSnapshot> snapshot, Character character)
        {
            // Keyed by item id plus, for equipment, upgrade level and craft quality - two swords of
            // the same id at +0 and +3 are different things, and the right one must go or come back.
            var target = snapshot
                .GroupBy(s => SnapshotKey(s.ItemId, s.UpgradeLevel, s.CraftQuality))
                .ToDictionary(g => g.Key, g => (Snap: g.First(), Amount: g.Sum(s => s.StackSize)));

            foreach (var key in Items.Select(KeyOf).Distinct().ToList())
            {
                int have = Items.Where(i => KeyOf(i) == key).Sum(i => i.StackSize);
                int toRemove = have - (target.TryGetValue(key, out var want) ? want.Amount : 0);
                if (toRemove <= 0) continue;

                foreach (var stack in Items.Where(i => KeyOf(i) == key).ToList())
                {
                    if (toRemove <= 0) break;
                    int take = Math.Min(toRemove, stack.StackSize);
                    if (stack.StackSize == take)
                    {
                        RemoveItem(stack);
                    }
                    else
                    {
                        stack.StackSize -= take;
                        ItemRemoved?.Invoke(this, new ItemReceivedEventArgs(stack.CloneOne(), take));
                    }
                    toRemove -= take;
                }
            }

            foreach (var (key, want) in target)
            {
                int have = Items.Where(i => KeyOf(i) == key).Sum(i => i.StackSize);
                int toAdd = want.Amount - have;
                if (toAdd <= 0) continue;

                if (ItemFactory.CreateItem(want.Snap.ItemId, toAdd, want.Snap.UpgradeLevel, want.Snap.CraftQuality) is { } item)
                    AddItem(item, character);
            }

            Restack();
        }

        /// <summary>
        /// Splits stacks larger than their item allows back into single items - repairs saves written
        /// while equipment wrongly stacked to 50 (each piece of a merged stack comes back with the
        /// stack's upgrade state; whatever a merge already overwrote can't be recovered). Returns how
        /// many items were split off. Called on every character load path.
        /// </summary>
        public int SplitOverfullStacks()
        {
            int split = 0;
            foreach (var item in Items.Where(i => i is EquipmentItem && i.StackSize > Math.Max(1, i.MaxStackSize)).ToList())
            {
                int index = Items.IndexOf(item);
                int extra = item.StackSize - 1;
                item.StackSize = 1;
                for (int n = 0; n < extra; n++)
                    Items.Insert(index + 1, item.CloneOne());
                split += extra;
            }
            return split;
        }

        private static (string Id, int Level, int Quality) SnapshotKey(string id, int upgradeLevel, float craftQuality) =>
            (id.ToLowerInvariant(), upgradeLevel, (int)Math.Round(craftQuality * 1000));

        private static (string Id, int Level, int Quality) KeyOf(Item item) =>
            item is EquipmentItem e ? SnapshotKey(e.Id, e.UpgradeLevel, e.CraftQuality) : SnapshotKey(item.Id, 0, 1f);

        public void Restack()
        {
            var grouped = Items
                .Where(i => i.MaxStackSize > 1)
                .GroupBy(i => i.Id)
                .ToList();

            foreach (var group in grouped)
            {
                var sorted = group.OrderByDescending(i => i.StackSize).ToList();
                var primary = sorted[0];

                for (int i = 1; i < sorted.Count; i++)
                {
                    var donor = sorted[i];
                    int space = primary.MaxStackSize - primary.StackSize;
                    int move = Math.Min(space, donor.StackSize);

                    primary.StackSize += move;
                    donor.StackSize -= move;

                    if (donor.StackSize <= 0)
                        Items.Remove(donor);
                }

            }

        }

    }

}
