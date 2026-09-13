using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Systems.Interfaces;
namespace Myria.Lib.Core.Systems
{
    public static class CombatSystem
    {
        private class PendingAction
        {
            public int TurnsRemaining { get; set; }
            public Action Execute { get; set; }
        }

        private static readonly Random _random = Random.Shared;
        public static bool TryHit(ICombatant attacker, ICombatant defender)
        {
            float aim = attacker.TotalAim;
            float evasion = defender.TotalEvasion;

            if (aim >= evasion)
            {
                return true; // Guaranteed hit
            }
            float hitChance = aim / evasion;
            float roll = (float)_random.NextDouble();

            return roll <= hitChance; // True = hit, False = miss
        }
        /// <summary>
        /// Controls how steeply damage scales with the attack/defense gap. Higher values make
        /// large stat differences more dramatic. Public/settable like RuneEvaluator's tuning
        /// constants, instead of a private const only editable by recompiling — a mod or a
        /// meaningfully different game can rebalance this at startup.
        /// </summary>
        public static float DamageSteepness { get; set; } = 0.8f;

        // Exponential damage formula: baseline is 40% of ATK at equal stats,
        // rises/falls exponentially as the gap grows. Never reaches zero.
        internal static float ExponentialDamage(float atk, float def)
        {
            float sum = atk + def;
            if (sum <= 0f) return 0f;
            return atk * 0.4f * MathF.Exp(DamageSteepness * (atk - def) / sum);
        }

        public static int CalculateDamage(ICombatant attacker, ICombatant defender)
        {
            float atk  = attacker.TotalPhysicalAttack;
            float matk = attacker.TotalMagicAttack;
            float def  = defender.TotalPhysicalDefense;
            float mdef = defender.TotalMagicDefense;

            if (!TryHit(attacker, defender)) return 0;

            float pdmg = ExponentialDamage(atk, def);
            float mdmg = ExponentialDamage(matk, mdef);
            float dmg  = Math.Max(pdmg, mdmg);

            if ((float)_random.NextDouble() < defender.GetBlockChance())
                dmg /= 2;

            return Math.Max(1, (int)dmg);
        }

        // Returns damage and whether it was a critical hit (player-facing attacks only).
        // Crits: 10% base + DEX/500 bonus, capped at 30%; deal 1.75× damage.
        public static (int Damage, bool IsCritical) CalculateDamageWithCrit(ICombatant attacker, ICombatant defender)
        {
            float atk  = attacker.TotalPhysicalAttack;
            float matk = attacker.TotalMagicAttack;
            float def  = defender.TotalPhysicalDefense;
            float mdef = defender.TotalMagicDefense;

            if (!TryHit(attacker, defender)) return (0, false);

            float pdmg = ExponentialDamage(atk, def);
            float mdmg = ExponentialDamage(matk, mdef);
            float dmg  = Math.Max(pdmg, mdmg);

            if ((float)_random.NextDouble() < defender.GetBlockChance())
                dmg /= 2;

            float critChance = Math.Min(0.30f, 0.10f + attacker.TotalDEX / 500f);
            bool  isCrit     = (float)_random.NextDouble() < critChance;
            if (isCrit) dmg *= 1.75f;

            return (Math.Max(1, (int)dmg), isCrit);
        }

    }

}