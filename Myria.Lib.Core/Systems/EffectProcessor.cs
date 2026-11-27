using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Effects;

namespace Myria.Lib.Core.Systems
{
    public static class EffectProcessor
    {
        /// <summary>
        /// Applies an effect to a target. If the same effect (by DefinitionId) is already active,
        /// its duration is refreshed to whichever is longer rather than stacking a duplicate.
        /// </summary>
        public static void Apply(CombatEntity target, ActiveEffect effect)
        {
            var existing = target.ActiveEffects
                .FirstOrDefault(e => e.DefinitionId == effect.DefinitionId);

            if (existing != null)
            {
                existing.TurnsRemaining = Math.Max(existing.TurnsRemaining, effect.TurnsRemaining);
                return;
            }

            target.ActiveEffects.Add(effect);
        }

        /// <summary>
        /// Processes one turn's worth of active effects on an entity.
        /// Deals poison damage, applies regen, and decrements all durations.
        /// Returns log entries describing what happened.
        /// </summary>
        public static List<CombatLogEntry> Tick(CombatEntity entity)
        {
            var log = new List<CombatLogEntry>();
            var toRemove = new List<ActiveEffect>();

            foreach (var effect in entity.ActiveEffects)
            {
                switch (effect.Type)
                {
                    case EffectType.Poison:
                    {
                        int dmg = Math.Max(1, (int)effect.Magnitude);
                        entity.TakeDamage(dmg);
                        log.Add(new CombatLogEntry("pg.fight.effect.poison", entity.Name, dmg));
                        break;
                    }
                    case EffectType.HpRegen:
                    {
                        int gain = Math.Max(1, CombatEntity.ScaleHeal((int)effect.Magnitude));
                        int actual = entity.Heal(gain);
                        if (actual > 0)
                            log.Add(new CombatLogEntry("pg.fight.effect.hpRegen", entity.Name, actual));
                        break;
                    }
                    case EffectType.ManaRegen:
                    {
                        int gain = Math.Max(1, (int)effect.Magnitude);
                        int actual = entity.RestoreMana(gain);
                        if (actual > 0)
                            log.Add(new CombatLogEntry("pg.fight.effect.manaRegen", entity.Name, actual));
                        break;
                    }
                    // Stun, Silence, StatBuff, Resurrection, LifeSteal, ManaSiphon: no per-tick action; duration ticks down.
                }

                effect.TurnsRemaining--;
                if (effect.TurnsRemaining <= 0)
                    toRemove.Add(effect);
            }

            foreach (var expired in toRemove)
            {
                entity.ActiveEffects.Remove(expired);
                log.Add(new CombatLogEntry("pg.fight.effect.expired", expired.Name, entity.Name));
            }

            return log;
        }

        /// <returns>True if entity is stunned and must skip its entire turn.</returns>
        public static bool IsStunned(CombatEntity entity)
            => entity.ActiveEffects.Any(e => e.Type == EffectType.Stun && e.TurnsRemaining > 0);

        /// <returns>True if entity is silenced and cannot use skills.</returns>
        public static bool IsSilenced(CombatEntity entity)
            => entity.ActiveEffects.Any(e => e.Type == EffectType.Silence && e.TurnsRemaining > 0);

        /// <summary>
        /// Checks whether a just-killed entity should be resurrected.
        /// Resurrection only triggers if <paramref name="hasLivingAllies"/> is true.
        /// If triggered: removes the Resurrection effect, restores HP to Magnitude% of max, returns true.
        /// </summary>
        public static bool TryResurrect(CombatEntity entity, bool hasLivingAllies)
        {
            if (!hasLivingAllies) return false;

            var rez = entity.ActiveEffects.FirstOrDefault(e => e.Type == EffectType.Resurrection);
            if (rez == null) return false;

            entity.ActiveEffects.Remove(rez);
            int rezHp = Math.Max(1, (int)(entity.MaxHealth * Math.Clamp(rez.Magnitude, 0f, 1f)));
            entity.Heal(rezHp);
            return true;
        }
    }
}
