using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Entities.Items
{
    public class EquipmentItem : Item
    {
        public string SlotType { get; set; } = Myria.Lib.Core.Systems.Enums.EquipmentType.Weapon;
        public override int BuyPrice { get; set; } = 300;

        /// <summary>Equipment doesn't stack: each piece carries its own upgrade level and craft
        /// quality. (It used to fall back to Item's default of 50, and since stacking only compared
        /// ids, a +3 sword merged into a +0 sword's stack and lost its upgrade.)</summary>
        public override int MaxStackSize { get; set; } = 1;

        /// <summary>Even with a MaxStackSize above 1 (mod data), only identical pieces stack.</summary>
        public override bool CanStackWith(Item other) =>
            base.CanStackWith(other) && other is EquipmentItem e
            && e.UpgradeLevel == UpgradeLevel && e.CraftQuality.Equals(CraftQuality);

        /// <summary>Base bonus values as defined in item data (before upgrades).</summary>
        public EquipmentBonuses BaseStats { get; set; }

        /// <summary>Current bonus values (scales with upgrade level).</summary>
        public EquipmentBonuses Bonuses { get; set; }

        public int UpgradeLevel { get; set; } = 0;
        public string? UpgradeCategory { get; set; }

        /// <summary>
        /// Quality multiplier baked in at craft/upgrade time from the blacksmith's skill level.
        /// Applied on top of the upgrade scaling so higher-skill work produces permanently stronger items.
        /// Default 1.0 for items that were not player-crafted (shops, loot).
        /// </summary>
        public float CraftQuality { get; set; } = 1f;

        // Convenience properties — delegate to Bonuses struct so existing UI code keeps working
        public int BonusHP      => Bonuses.HP;
        public int BonusMP      => Bonuses.MP;
        public int BonusSTR     => Bonuses.STR;
        public int BonusDEX     => Bonuses.DEX;
        public int BonusEND     => Bonuses.END;
        public int BonusINT     => Bonuses.INT;
        public int BonusSPR     => Bonuses.SPR;
        public int BonusATK     => Bonuses.ATK;
        public int BonusDEF     => Bonuses.DEF;
        public int BonusMATK    => Bonuses.MATK;
        public int BonusMDEF    => Bonuses.MDEF;
        public int BonusAim     => Bonuses.Aim;
        public int BonusEvasion => Bonuses.Evasion;
        public float BonusCrit  => Bonuses.Crit;
        public float BonusBlock => Bonuses.Block;

        public bool IsUsableBy(Character character) =>
            AllowedClasses.Count == 0
            || AllowedClasses.Any(c => c.Equals(character.Class, StringComparison.OrdinalIgnoreCase));

        public override void Use(Character character) { }

        /// <summary>
        /// Puts a fresh item (from ItemFactory) into a saved upgrade state: the craft quality first,
        /// then each upgrade level re-applied - exactly how the bonuses were reached in play. Used by
        /// every load/sync path (local saves, the realm database, server pushes).
        /// </summary>
        public void RestoreUpgradeState(int upgradeLevel, float craftQuality)
        {
            CraftQuality = craftQuality > 0 ? craftQuality : 1f;
            UpgradeLevel = 0;
            Bonuses = BaseStats;
            for (int i = 0; i < upgradeLevel; i++)
                TryUpgrade_Internal();
        }

        /// <summary>
        /// Applies one upgrade level, scaling Bonuses from BaseStats.
        /// Called both from the UI upgrade flow and from save-file reload (ItemConverter).
        /// </summary>
        public bool TryUpgrade(Character character, int maxUpgradeLevel = 10)
        {
            if (UpgradeLevel >= maxUpgradeLevel) return false;
            TryUpgrade_Internal();
            return true;
        }

        /// <summary>
        /// Applies the next upgrade level without consuming materials.
        /// Used by <see cref="TryUpgrade"/> and by save-file deserialization.
        /// </summary>
        public void TryUpgrade_Internal()
        {
            UpgradeLevel++;
            Bonuses = BaseStats.Scale(UpgradeMultiplier(UpgradeLevel) * CraftQuality);
        }

        /// <summary>
        /// Stat multiplier per upgrade level (v0.3): +10 is x2.7. It used to climb to x11, which made
        /// maxed normal gear trivialise dungeons - and stacked with craft quality (up to x3) to x33.
        /// Maxed normal gear should make a dungeon of your level hard but possible solo; a master
        /// smith's quality on top is what makes the strongest gear.
        /// </summary>
        public static float UpgradeMultiplier(int upgradeLevel) =>
            upgradeLevel <= 0 ? 1f
            : upgradeLevel < UpgradeMultipliers.Length ? UpgradeMultipliers[upgradeLevel]
            : UpgradeMultipliers[^1] + 0.3f * (upgradeLevel - UpgradeMultipliers.Length + 1);

        private static readonly float[] UpgradeMultipliers =
            { 1f, 1.1f, 1.2f, 1.3f, 1.45f, 1.6f, 1.75f, 1.95f, 2.15f, 2.4f, 2.7f };
    }
}
