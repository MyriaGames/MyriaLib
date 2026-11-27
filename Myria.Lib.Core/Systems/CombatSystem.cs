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
        public static bool TryHit(ICombatant attacker, ICombatant defender) => TryHit(attacker, defender, _random);

        /// <summary><see cref="TryHit(ICombatant, ICombatant)"/> with the caller's Random (Mono's seeded,
        /// reproducible realm).</summary>
        public static bool TryHit(ICombatant attacker, ICombatant defender, Random rng)
        {
            float hitChance = HitChance(attacker.TotalAim, defender.TotalEvasion, defender.InstinctDodge);
            if (hitChance >= 1f) return true;   // guaranteed hit
            return (float)rng.NextDouble() <= hitChance;
        }

        /// <summary>
        /// Chance that an attack with <paramref name="aim"/> hits a defender with <paramref name="evasion"/>.
        /// The same rule for players and monsters: aim at or above evasion always hits, below it the
        /// chance is aim / evasion. A defender with an Instinct passive (<see cref="ICombatant.InstinctDodge"/>,
        /// the Rogue and the Hunter) always keeps some dodge chance on top: instinct × evasion / aim,
        /// never more than the instinct itself - so it shrinks against very accurate attackers but never
        /// reaches zero, and it joins the normal rule without a jump at aim = evasion.
        /// </summary>
        public static float HitChance(float aim, float evasion, float instinct = 0f)
        {
            float hit = aim >= evasion ? 1f : aim / evasion;
            if (instinct > 0f)
            {
                float dodge = instinct * (aim <= 0f ? 1f : MathF.Min(1f, evasion / aim));
                hit = MathF.Min(hit, 1f - dodge);
            }
            return Math.Clamp(hit, 0f, 1f);
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

        public static int CalculateDamage(ICombatant attacker, ICombatant defender) => CalculateDamage(attacker, defender, _random);

        /// <summary>Basic attack damage (0 = miss) with the caller's Random.</summary>
        public static int CalculateDamage(ICombatant attacker, ICombatant defender, Random rng)
        {
            float atk  = attacker.TotalPhysicalAttack;
            float matk = attacker.TotalMagicAttack;
            float def  = defender.TotalPhysicalDefense;
            float mdef = defender.TotalMagicDefense;

            if (!TryHit(attacker, defender, rng)) return 0;

            float pdmg = ExponentialDamage(atk, def);
            float mdmg = ExponentialDamage(matk, mdef);
            float dmg  = Math.Max(pdmg, mdmg);

            if ((float)rng.NextDouble() < defender.GetBlockChance())
                dmg /= 2;

            if (attacker is Myria.Lib.Core.Entities.Monsters.Monster { DamageScale: not 1f } scaled)
                dmg *= scaled.DamageScale;

            return Math.Max(1, (int)dmg);
        }

        // Returns damage and whether it was a critical hit (player-facing attacks only).
        // Crits: 10% base + DEX/500 bonus, capped at 30%; deal 1.75× damage.
        public static (int Damage, bool IsCritical) CalculateDamageWithCrit(ICombatant attacker, ICombatant defender) =>
            CalculateDamageWithCrit(attacker, defender, _random);

        /// <summary>Basic attack damage with crits (0 = miss) with the caller's Random.</summary>
        public static (int Damage, bool IsCritical) CalculateDamageWithCrit(ICombatant attacker, ICombatant defender, Random rng)
        {
            float atk  = attacker.TotalPhysicalAttack;
            float matk = attacker.TotalMagicAttack;
            float def  = defender.TotalPhysicalDefense;
            float mdef = defender.TotalMagicDefense;

            if (!TryHit(attacker, defender, rng)) return (0, false);

            float pdmg = ExponentialDamage(atk, def);
            float mdmg = ExponentialDamage(matk, mdef);
            float dmg  = Math.Max(pdmg, mdmg);

            if ((float)rng.NextDouble() < defender.GetBlockChance())
                dmg /= 2;

            float critChance = Math.Min(0.30f, 0.10f + attacker.TotalDEX / 500f);
            bool  isCrit     = (float)rng.NextDouble() < critChance;
            if (isCrit) dmg *= 1.75f;

            return (Math.Max(1, (int)dmg), isCrit);
        }


        // ── Skill rules (shared by solo, group and PvP fights and Mono) ─────────

        /// <summary>The value a skill's damage/heal scales from: ATK, MATK, DexterityAttack, ... (STR/INT
        /// skills scale like ATK/MATK skills, v0.3).</summary>
        public static int SkillBaseStat(Character caster, Skill skill) =>
            skill.StatToScaleFrom.ToUpperInvariant() switch
            {
                "ATK"  => caster.TotalPhysicalAttack,
                "MATK" => caster.TotalMagicAttack,
                "SPR"  => caster.TotalSPR,
                "INT"  => caster.TotalMagicAttack,
                "DEX"  => caster.DexterityAttack,
                "AIM"  => caster.TotalAim * 2,
                "EVA"  => caster.TotalEvasion * 2,
                "END"  => caster.TotalEND,
                "STR"  => caster.TotalPhysicalAttack,
                _      => caster.TotalPhysicalAttack
            };

        /// <summary>The stat a skill's effects scale with (effect magnitude × ScalingFactor × stat / 10):
        /// the caster's primary stat, so ATK/MATK map back to STR/INT.</summary>
        public static int SkillEffectStat(Character caster, Skill skill) =>
            skill.StatToScaleFrom.ToUpperInvariant() switch
            {
                "ATK"                   => caster.TotalSTR,
                "MATK"                  => caster.TotalINT,
                "SPR"                   => caster.TotalSPR,
                "INT"                   => caster.TotalINT,
                "DEX" or "AIM" or "EVA" => caster.TotalDEX,
                "END"                   => caster.TotalEND,
                "STR"                   => caster.TotalSTR,
                _                       => caster.TotalSTR
            };

        /// <summary>
        /// Damage a skill does to <paramref name="target"/> (computed, not applied; 0 for healing skills):
        /// raw = base stat × ScalingFactor, damage = raw² / (raw + DEF or MDEF), at least 1. The rule of
        /// solo, group and PvP fights (v0.3).
        /// </summary>
        public static int SkillDamage(Character caster, Skill skill, ICombatant target)
        {
            if (skill.IsHealing) return 0;
            float raw = SkillBaseStat(caster, skill) * skill.ScalingFactor;
            float def = skill.Type == SkillType.Physical ? target.TotalPhysicalDefense : target.TotalMagicDefense;
            if (raw <= 0f) return 1;
            return Math.Max(1, (int)(raw * (raw / (raw + def))));
        }

        /// <summary>A healing skill's direct heal (computed, not applied): base stat × ScalingFactor,
        /// through <see cref="Entities.CombatEntity.ScaleHeal"/>. 0 for non-healing skills.</summary>
        public static int SkillHeal(Character caster, Skill skill) =>
            skill.IsHealing ? Entities.CombatEntity.ScaleHeal((int)(SkillBaseStat(caster, skill) * skill.ScalingFactor)) : 0;
    }

}