using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Models.BaseModel
{
    // JSON deserialization model — maps 1:1 to items.json entries
    public class GameItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }    // "consumable", "equipment", "material"
        public string Rarity { get; set; } = "Common";
        public int BuyPrice { get; set; }
        public int StackSize { get; set; } = 1;
        public int MaxStackSize { get; set; } = 1;
        public string? ToolType { get; set; } = null;
        public List<string> AllowedClasses { get; set; } = new();
        public string? UpgradeCategory { get; set; }

        // Consumable fields
        public int HealAmount { get; set; }
        /// <summary>Heal as a percentage of max HP (e.g. 7.5); used instead of HealAmount when set.</summary>
        public double HealPercent { get; set; }
        public int ManaRestore { get; set; }
        public string? UseEffect { get; set; }

        // Equipment fields
        // Raw JSON value (items.json stores this as a plain int, e.g. 2) — converted to the new
        // string-based EquipmentType via EquipmentType.FromLegacyInt when building an EquipmentItem.
        public int SlotType { get; set; }
        public int BaseBonusHP { get; set; }
        public int BaseBonusMP { get; set; }
        public int BaseBonusSTR { get; set; }
        public int BaseBonusDEX { get; set; }
        public int BaseBonusEND { get; set; }
        public int BaseBonusINT { get; set; }
        public int BaseBonusSPR { get; set; }
        public int BaseBonusATK { get; set; }
        public int BaseBonusDEF { get; set; }
        public int BaseBonusMATK { get; set; }
        public int BaseBonusMDEF { get; set; }
        public int BaseBonusAim { get; set; }
        public int BaseBonusEvasion { get; set; }
        public float BaseBonusCrit { get; set; }
        public float BaseBonusBlock { get; set; }
    }
}
