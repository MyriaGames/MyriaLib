using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Interfaces;
using System.Text.Json.Serialization;

namespace Myria.Lib.Core.Entities
{
    public abstract class CombatEntity : ICombatant
    {
        public string Name { get; set; }
        public Stats Stats { get; set; } = new();
        public int CurrentHealth { get; set; }
        public int CurrentMana { get; set; }

        [JsonIgnore]
        public List<ActiveEffect> ActiveEffects { get; } = new();

        // Override in subclasses to inject class-level stat bonuses
        protected virtual int ExtraSTR => 0;
        protected virtual int ExtraDEX => 0;
        protected virtual int ExtraEND => 0;
        protected virtual int ExtraINT => 0;
        protected virtual int ExtraSPR => 0;
        protected virtual int ExtraBaseHealth => 0;
        protected virtual int ExtraBaseMana   => 0;

        // Returns the flat stat bonus granted by currently active StatBuff effects.
        public int GetEffectStatBonus(string stat) => ActiveEffects
            .Where(e => e.Type == EffectType.StatBuff
                     && e.StatName.Equals(stat, StringComparison.OrdinalIgnoreCase))
            .Sum(e => (int)e.Magnitude);

        // Full stat totals — race base + player investment + class bonus + gear bonuses + active effects
        public int TotalSTR => Stats.TotalStrength    + ExtraSTR + GetBonusFromGear(g => g.Bonuses.STR) + GetEffectStatBonus("STR");
        public int TotalDEX => Stats.TotalDexterity   + ExtraDEX + GetBonusFromGear(g => g.Bonuses.DEX) + GetEffectStatBonus("DEX");
        public int TotalEND => Stats.TotalEndurance   + ExtraEND + GetBonusFromGear(g => g.Bonuses.END) + GetEffectStatBonus("END");
        public int TotalINT => Stats.TotalIntelligence + ExtraINT + GetBonusFromGear(g => g.Bonuses.INT) + GetEffectStatBonus("INT");
        public int TotalSPR => Stats.TotalSpirit      + ExtraSPR + GetBonusFromGear(g => g.Bonuses.SPR) + GetEffectStatBonus("SPR");

        // MaxHealth/MaxMana live here so gear HP/MP bonuses are included
        public int MaxHealth => Stats.BaseHealth + ExtraBaseHealth + TotalEND * 5 + GetBonusFromGear(g => g.Bonuses.HP);
        public int MaxMana   => Stats.BaseMana   + ExtraBaseMana   + TotalSPR * 5 + GetBonusFromGear(g => g.Bonuses.MP);

        public bool IsAlive => CurrentHealth > 0;

        // Genuinely extensible backing store — a mod can add any slot-type key at runtime and
        // GetBonusFromGear picks it up immediately. Not itself serialized: the 3 named properties
        // below are the JSON wire format (existing save files have "WeaponSlot"/"ArmorSlot"/
        // "AccessorySlot" as top-level keys with no "Equipped" key), so their setters are what
        // actually populate this dictionary on load. A slot added only via this dictionary won't
        // survive save/load or appear in any UI yet — that needs matching DB/UI/ItemFactory work.
        [JsonIgnore]
        public Dictionary<string, EquipmentItem?> Equipped { get; } = new(StringComparer.OrdinalIgnoreCase);

        public EquipmentItem? WeaponSlot
        {
            get => Equipped.GetValueOrDefault(EquipmentType.Weapon);
            set => Equipped[EquipmentType.Weapon] = value;
        }
        public EquipmentItem? ArmorSlot
        {
            get => Equipped.GetValueOrDefault(EquipmentType.Armor);
            set => Equipped[EquipmentType.Armor] = value;
        }
        public EquipmentItem? AccessorySlot
        {
            get => Equipped.GetValueOrDefault(EquipmentType.Accessory);
            set => Equipped[EquipmentType.Accessory] = value;
        }

        /// <summary>
        /// Each combat value comes from one stat (v0.3): STR -> ATK, END -> DEF, INT -> MATK,
        /// SPR -> MDEF, x3 each. It used to be 2x the main stat plus the other one (ATK = 2 STR +
        /// END), so tanky END builds hit as hard as strength builds; x3 keeps the old ATK+DEF total.
        /// HP still comes from END and MP from SPR (see MaxHealth/MaxMana).
        /// </summary>
        public const int StatToCombatValue = 3;

        public int TotalPhysicalAttack  => TotalSTR * StatToCombatValue + GetBonusFromGear(g => g.Bonuses.ATK)  + GetEffectStatBonus("ATK");
        public int TotalPhysicalDefense => TotalEND * StatToCombatValue + GetBonusFromGear(g => g.Bonuses.DEF)  + GetEffectStatBonus("DEF");
        public int TotalMagicAttack     => TotalINT * StatToCombatValue + GetBonusFromGear(g => g.Bonuses.MATK) + GetEffectStatBonus("MATK");
        public int TotalMagicDefense    => TotalSPR * StatToCombatValue + GetBonusFromGear(g => g.Bonuses.MDEF) + GetEffectStatBonus("MDEF");

        /// <summary>What DEX-scaled skills scale from: the ATK shape built on DEX instead of STR
        /// (1.5x DEX plus the weapon/gear ATK). Raw DEX left them far behind ATK-scaled skills; 3x
        /// overshot (a Rogue did 70% of a group's damage). 1.5 puts the Hunter level with the Archer.</summary>
        public int DexterityAttack      => (int)(TotalDEX * DexSkillMultiplier) + GetBonusFromGear(g => g.Bonuses.ATK)  + GetEffectStatBonus("ATK");

        /// <summary>DEX per point of <see cref="DexterityAttack"/> (tuned by simulation).</summary>
        public static float DexSkillMultiplier { get; set; } = 1.5f;
        public int TotalAim             => TotalDEX + GetBonusFromGear(g => g.Bonuses.Aim)                   + GetEffectStatBonus("AIM");
        public int TotalEvasion         => (int)(TotalDEX * 0.85f) + GetBonusFromGear(g => g.Bonuses.Evasion) + GetEffectStatBonus("EVA");

        public float CritChance =>
            GetBonusFromGear(g => g.Bonuses.DEX) * 0.1f + GetBonusFromGear(g => g.Bonuses.Crit);

        public float BlockChance
        {
            get
            {
                float blockRaw = (GetBonusFromGear(g => g.Bonuses.END) * 0.3f)
                               + (GetBonusFromGear(g => g.Bonuses.INT) * 0.2f)
                               + (GetBonusFromGear(g => g.Bonuses.STR) * 0.1f)
                               + GetBonusFromGear(g => g.Bonuses.Block);
                return MathF.Min(blockRaw * 0.01f, 0.75f);
            }
        }

        public virtual void TakeDamage(int amount)
        {
            CurrentHealth = Math.Max(0, CurrentHealth - amount);
        }

        public virtual int Heal(int amount, string? source = null)
        {
            int actual = Math.Min(Math.Max(0, amount), MaxHealth - CurrentHealth);
            if (actual > 0) CurrentHealth += actual;
            return actual;
        }

        public virtual int RestoreMana(int amount, string? source = null)
        {
            int actual = Math.Min(Math.Max(0, amount), MaxMana - CurrentMana);
            if (actual > 0) CurrentMana += actual;
            return actual;
        }

        public virtual int DealPhysicalDamage() => TotalPhysicalAttack;
        public virtual int DefandPhysical()      => TotalPhysicalDefense;
        public virtual float GetBlockChance()    => BlockChance;

        public int GetBonusFromGear(Func<EquipmentItem, int> selector)
            => Equipped.Values.Where(i => i != null).Sum(i => selector(i!));

        public float GetBonusFromGear(Func<EquipmentItem, float> selector)
            => Equipped.Values.Where(i => i != null).Sum(i => selector(i!));
    }
}
