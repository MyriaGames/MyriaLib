using System.Text.Json;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Models.BaseModel;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Mods;

namespace Myria.Lib.Core.Services.Builder
{
    public static class ItemFactory
    {
        private static Dictionary<string, GameItem> _itemDefs = new();

        public static void LoadItems(string path = "Data/common/items.json")
        {
            string json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<GameItem>>(json) ?? [];
            LoadItems(list);
        }

        /// <summary>Loads item definitions from already-parsed data (e.g. read from a database).</summary>
        public static void LoadItems(List<GameItem> list)
        {
            _itemDefs = list.ToDictionary(i => i.Id, i => i);
        }

        /// <summary>
        /// Returns the display name for an item ID without allocating an item instance.
        /// Returns <c>null</c> if the ID is not found (no error logged — use for soft checks).
        /// </summary>
        public static string? GetItemName(string id) =>
            _itemDefs.TryGetValue(id, out var def) ? def.Name : null;

        /// <summary>
        /// Creates an item by ID. Returns <c>null</c> and logs an error if the ID is not found.
        /// Use <see cref="TryCreateItem"/> for silent existence checks.
        /// </summary>
        public static Item? CreateItem(string id, int stackSize = 1)
        {
            if (!_itemDefs.TryGetValue(id, out var def))
            {
                GameLog.Error($"Item '{id}' not found in loaded item data.");
                return null;
            }

            Item item = def.Type switch
            {
                "consumable"           => BuildConsumable(def),
                "equipment"            => BuildEquipment(def),
                "material"             => BuildMaterial(def),
                "inventory_expansion"  => BuildInventoryExpansion(def),
                _ => throw new Exception($"Unsupported item type: {def.Type}")
            };

            // def.Rarity is already a plain string (GameItem.Rarity) — assign directly instead of
            // validating against a closed enum, so mod-defined rarity tiers beyond the 7 built-in
            // ones (see ItemRarity.AllBuiltIn) work without being silently rejected.
            if (!string.IsNullOrEmpty(def.Rarity))
                item.Rarity = def.Rarity;

            item.StackSize = stackSize > 1 ? stackSize : (def.StackSize > 0 ? def.StackSize : 1);
            return item;
        }

        /// <summary>
        /// Tries to create an item by ID. Returns <c>false</c> silently if not found.
        /// Use this when absence is an acceptable outcome (e.g. optional loot).
        /// </summary>
        public static bool TryCreateItem(string id, out Item? item)
        {
            if (!_itemDefs.TryGetValue(id, out var def))
            {
                item = null;
                return false;
            }
            item = CreateItem(id); // def exists, so CreateItem will not return null
            return item != null;
        }

        public static List<Item> GetAllItemsFor(Character character)
        {
            return _itemDefs.Values
                .Where(def => def.Type == "equipment"
                    && (def.AllowedClasses.Count == 0
                        || def.AllowedClasses.Any(c => c.Equals(character.Class, StringComparison.OrdinalIgnoreCase))))
                .Select(def => CreateItem(def.Id)!)
                .ToList();
        }

        public static List<Item> GetSmithItemsFor(Character character)
        {
            return _itemDefs.Values
                .Where(def => def.Type == "equipment"
                    && def.AllowedClasses.Any(c => c.Equals(character.Class, StringComparison.OrdinalIgnoreCase))
                    && def.Rarity == "Common")
                .Select(def => CreateItem(def.Id)!)
                .ToList();
        }

        private static ConsumableItem BuildConsumable(GameItem def) => new()
        {
            Id          = def.Id,
            Name        = def.Name,
            Description = def.Description,
            BuyPrice    = def.BuyPrice,
            MaxStackSize = def.MaxStackSize,
            HealAmount  = def.HealAmount,
            ManaRestore = def.ManaRestore,
            UseEffect   = def.UseEffect,
            AllowedClasses = def.AllowedClasses ?? new(),
        };

        private static MaterialItem BuildMaterial(GameItem def) => new()
        {
            Id          = def.Id,
            Name        = def.Name,
            Description = def.Description,
            BuyPrice    = def.BuyPrice,
            MaxStackSize = def.MaxStackSize,
            ToolType    = def.ToolType,
            AllowedClasses = def.AllowedClasses ?? new(),
        };

        private static EquipmentItem BuildEquipment(GameItem def)
        {
            var baseStats = new EquipmentBonuses
            {
                HP      = def.BaseBonusHP,
                MP      = def.BaseBonusMP,
                STR     = def.BaseBonusSTR,
                DEX     = def.BaseBonusDEX,
                END     = def.BaseBonusEND,
                INT     = def.BaseBonusINT,
                SPR     = def.BaseBonusSPR,
                ATK     = def.BaseBonusATK,
                DEF     = def.BaseBonusDEF,
                MATK    = def.BaseBonusMATK,
                MDEF    = def.BaseBonusMDEF,
                Aim     = def.BaseBonusAim,
                Evasion = def.BaseBonusEvasion,
                Crit    = def.BaseBonusCrit,
                Block   = def.BaseBonusBlock,
            };

            return new EquipmentItem
            {
                Id               = def.Id,
                Name             = def.Name,
                Description      = def.Description,
                BuyPrice         = def.BuyPrice,
                MaxStackSize     = Math.Max(1, def.MaxStackSize), // never Item's default of 50 (equipment carries its own upgrade state)
                SlotType         = EquipmentType.FromLegacyInt.GetValueOrDefault(def.SlotType, EquipmentType.Weapon),
                ToolType         = def.ToolType,
                IsTool           = def.ToolType != null,
                UpgradeCategory  = def.UpgradeCategory,
                AllowedClasses   = def.AllowedClasses ?? new(),
                BaseStats        = baseStats,
                Bonuses          = baseStats,    // starts equal to base; scales on upgrade
            };
        }

        private static InventoryExpansion BuildInventoryExpansion(GameItem def) => new()
        {
            Id          = def.Id,
            Name        = def.Name,
            Description = def.Description,
            BuyPrice    = def.BuyPrice,
            AllowedClasses = def.AllowedClasses ?? new(),
        };

    }
}
