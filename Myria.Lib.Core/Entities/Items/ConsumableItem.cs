using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Mods;

namespace Myria.Lib.Core.Entities.Items
{
    public class ConsumableItem : Item
    {
        public int HealAmount { get; set; }
        /// <summary>Heal as a percentage of max HP (healing potions, v0.3) - a flat amount fell far
        /// behind HP growth (25 HP was under 2% of a level-10 fighter). Wins over HealAmount.</summary>
        public double HealPercent { get; set; }
        public int ManaRestore { get; set; }

        /// <summary>The HP this item heals <paramref name="character"/> for (before the max-HP cap).</summary>
        public int HealFor(Character character) =>
            HealPercent > 0 ? (int)Math.Ceiling(character.MaxHealth * HealPercent / 100.0) : HealAmount;
        public string? UseEffect { get; set; }
        /// <summary>
        /// uses the item and grants its effects
        /// </summary>
        /// <param name="player">player character</param>
        public override void Use(Character character)
        {
            if (ModItemUseEffectRegistry.TryUse(this, character))
            {
                GameEvents.FireItemUsed(character, this);
                return;
            }

            ApplyBaseEffect(character);
            GameEvents.FireItemUsed(character, this);
        }

        public void ApplyBaseEffect(Character character)
        {
            int heal = HealFor(character);
            if (heal > 0)
                character.Heal(heal, Id);

            if (ManaRestore > 0)
                character.RestoreMana(ManaRestore, Id);
        }

    }

}
