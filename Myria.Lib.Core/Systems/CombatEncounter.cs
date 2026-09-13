using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Events;
using Myria.Lib.Core.Systems.Interfaces;

namespace Myria.Lib.Core.Systems
{
    public sealed class CombatEncounter
    {
        public Character Character { get; }
        public Monster Enemy { get; }

        public CombatPhase Phase { get; private set; } = CombatPhase.CharacterTurn;

        public int TurnsUntilActionExecutes { get; private set; } = 0;
        public int RecoveryTurnsRemaining { get; private set; } = 0;

        // Per-skill cooldowns for this fight only - reset every new encounter (never persisted on
        // Character), independent of RecoveryTurnsRemaining above (that's a universal "can the
        // character act at all" timer; this is "is THIS specific skill available"). Stored as
        // Cooldown+1 and decremented once per completed player action (see TickSkillCooldowns) so
        // a skill used on turn N is actually unusable for skill.Cooldown full turns afterward,
        // not skill.Cooldown-1.
        private readonly Dictionary<string, int> _skillCooldowns = new();

        public int GetSkillCooldownRemaining(string skillId) =>
            _skillCooldowns.TryGetValue(skillId, out var v) ? Math.Max(0, v - 1) : 0;

        public List<CombatLogEntry> Log { get; } = new();

        private Action? _pendingAction;
        private Dictionary<string, int> _dropnumbers = new Dictionary<string, int>();
        private List<Item> _drops = new List<Item>();
        public bool InventoryFull { get; set; } = false;
        public event EventHandler<MonsterKilledEventArgs>? MonsterKilled;

        /// <summary>
        /// The actual (level-scaled) XP granted to <see cref="Character"/> on victory — 0 until
        /// the encounter finishes. Read this instead of <see cref="Enemy"/>.Exp for anything
        /// XP-derived (UI display, bonus calculations): Enemy.Exp is the monster's raw base
        /// value before ScaleXp's level-difference scaling is applied.
        /// </summary>
        public long LastXpGained { get; private set; }

        public CombatEncounter(Character character, Monster enemy)
        {
            Character = character;
            Enemy = enemy;
            Enemy.ResetHealth(); // if you do that elsewhere, remove
            Log.Add(new CombatLogEntry("pg.fight.log.start", enemy.Name));
        }

        public void CharacterAttack()
        {
            if (Phase != CombatPhase.CharacterTurn) return;

            bool characterStunned = EffectProcessor.IsStunned(Character);
            foreach (var entry in EffectProcessor.Tick(Character))
                Log.Add(entry);

            if (!Character.IsAlive) { FinishCharacterLost(); return; }

            if (characterStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", Character.Name));
                EndCharacterAction();
                return;
            }

            var (dmg, isCrit) = CombatSystem.CalculateDamageWithCrit(Character, Enemy);
            if (dmg <= 0)
                Log.Add(new CombatLogEntry("pg.fight.log.miss", Character.Name));
            else
            {
                Enemy.TakeDamage(dmg);
                Log.Add(isCrit
                    ? new CombatLogEntry("pg.fight.log.critHit", Character.Name, dmg)
                    : new CombatLogEntry("pg.fight.log.hit", Character.Name, dmg));
            }

            EndCharacterAction();
        }

        public bool CharacterBeginCast(Skill skill)
        {
            if (Phase != CombatPhase.CharacterTurn) return false;

            bool characterStunned = EffectProcessor.IsStunned(Character);
            foreach (var entry in EffectProcessor.Tick(Character))
                Log.Add(entry);

            if (!Character.IsAlive) { FinishCharacterLost(); return false; }

            if (characterStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", Character.Name));
                EndCharacterAction();
                return false;
            }

            if (EffectProcessor.IsSilenced(Character))
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.silenced", Character.Name));
                return false;
            }

            if (Character.CurrentMana < skill.ManaCost)
            {
                Log.Add(new CombatLogEntry("pg.fight.log.nomana"));
                return false;
            }

            int cooldownRemaining = GetSkillCooldownRemaining(skill.Id);
            if (cooldownRemaining > 0)
            {
                Log.Add(new CombatLogEntry("pg.fight.log.onCooldown", skill.Name, cooldownRemaining));
                return false;
            }

            if (skill.CastTime > 0)
            {
                Phase = CombatPhase.Casting;
                TurnsUntilActionExecutes = skill.CastTime;

                _pendingAction = () =>
                {
                    ExecuteSkill(skill);
                    RecoveryTurnsRemaining = skill.RecoveryTime;
                };

                Log.Add(new CombatLogEntry("pg.fight.log.beginCast", Character.Name, skill.Name));
            }
            else
            {
                ExecuteSkill(skill);
                RecoveryTurnsRemaining = skill.RecoveryTime;
                EndCharacterAction();
            }

            return true;
        }

        /// <summary>
        /// Use a consumable item in combat. Consumes the item immediately;
        /// the enemy gets one free attack in the recovery turn before the player can act again.
        /// </summary>
        public bool CharacterUseItem(ConsumableItem item)
        {
            if (Phase != CombatPhase.CharacterTurn) return false;

            bool characterStunned = EffectProcessor.IsStunned(Character);
            foreach (var entry in EffectProcessor.Tick(Character))
                Log.Add(entry);

            if (!Character.IsAlive) { FinishCharacterLost(); return false; }

            if (characterStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", Character.Name));
                EndCharacterAction();
                return false;
            }

            item.Use(Character);
            Character.Inventory.RemoveItem(item);
            Log.Add(new CombatLogEntry("pg.fight.log.usedItem", Character.Name, item.Name));

            RecoveryTurnsRemaining = 1;
            EndCharacterAction();
            return true;
        }

        public void Tick() // advances time by 1 "round"
        {
            if (Phase == CombatPhase.Finished) return;

            // casting countdown
            if (Phase == CombatPhase.Casting)
            {
                TurnsUntilActionExecutes--;
                if (TurnsUntilActionExecutes <= 0)
                {
                    _pendingAction?.Invoke();
                    _pendingAction = null;
                    TickSkillCooldowns();

                    if (Enemy.IsAlive) EnemyTurn();
                    else FinishCharacterWon();
                }
                return;
            }

            // recovery countdown
            if (Phase == CombatPhase.Recovery)
            {
                RecoveryTurnsRemaining--;
                if (RecoveryTurnsRemaining <= 0)
                    Phase = CombatPhase.CharacterTurn;
                else
                    EnemyTurn();
            }

        }

        private void ExecuteSkill(Skill skill)
        {
            Character.SpendMana(skill.ManaCost);
            if (skill.Cooldown > 0) _skillCooldowns[skill.Id] = skill.Cooldown + 1;
            SkillLevelingService.GrantUsage(Character, skill.Id);

            // In solo encounters AllAllies and SingleAlly both resolve to the character themselves.
            var primaryTarget = (skill.Target == SkillTarget.Self
                              || skill.Target == SkillTarget.SingleAlly
                              || skill.Target == SkillTarget.AllAllies)
                ? (Myria.Lib.Core.Entities.CombatEntity)Character
                : Enemy;

            int damageDealt = 0;

            switch (skill.Target)
            {
                case SkillTarget.Self:
                case SkillTarget.SingleAlly:
                case SkillTarget.AllAllies:
                    ExecuteSkillOnSelf(skill);
                    break;

                case SkillTarget.AllEnemies:
                case SkillTarget.SingleEnemy:
                default:
                    damageDealt = ExecuteSkillOnEnemy(skill, Enemy);
                    break;
            }

            // Apply data-driven effects.
            int effectStat = ResolveEffectStat(skill);
            foreach (var entry in skill.Effects)
            {
                var effectHost = entry.ApplyTo == EffectTarget.Caster || entry.ApplyTo == EffectTarget.AllAllies
                    ? (Myria.Lib.Core.Entities.CombatEntity)Character
                    : primaryTarget;

                var effect = EffectFactory.CreateEffect(entry.EffectId, skill.Id, skill.ScalingFactor, effectStat);
                if (effect == null) continue;

                if (effect.Type == EffectType.LifeSteal)
                {
                    int healAmount = Math.Max(1, (int)(damageDealt * effect.Magnitude));
                    Character.Heal(healAmount);
                    Log.Add(new CombatLogEntry("pg.fight.effect.lifesteal", Character.Name, healAmount));
                }
                else if (effect.Type == EffectType.ManaSiphon)
                {
                    int manaAmount = Math.Max(1, (int)(damageDealt * effect.Magnitude));
                    Character.RestoreMana(manaAmount);
                    Log.Add(new CombatLogEntry("pg.fight.effect.manasiphon", Character.Name, manaAmount));
                }
                else
                {
                    EffectProcessor.Apply(effectHost, effect);
                    Log.Add(new CombatLogEntry("pg.fight.effect.applied", effect.Name, effectHost.Name));
                }
            }

            // Optional code-defined effect hook.
            skill.Effect?.Invoke(Character, Enemy);
        }

        private void ExecuteSkillOnSelf(Skill skill)
        {
            if (skill.IsHealing)
            {
                int baseStat = ResolveSkillBaseStat(skill);
                int heal = (int)(baseStat * skill.ScalingFactor);
                int healed = Math.Min(heal, Character.MaxHealth - Character.CurrentHealth);
                Character.Heal(healed);
                Log.Add(new CombatLogEntry("pg.fight.log.heal", Character.Name, healed));
            }
            // Non-healing Self/Ally/AllAllies skills rely entirely on their Effects entries.
        }

        private int ExecuteSkillOnEnemy(Skill skill, ICombatant target)
        {
            if (skill.IsHealing) return 0;

            int baseStat = ResolveSkillBaseStat(skill);
            float raw = baseStat * skill.ScalingFactor;
            float def = skill.Type == SkillType.Physical
                ? target.TotalPhysicalDefense
                : target.TotalMagicDefense;
            int dmg = Math.Max(1, (int)CombatSystem.ExponentialDamage(raw, def));
            target.TakeDamage(dmg);
            Log.Add(new CombatLogEntry("pg.fight.log.skillHit", Character.Name, skill.Name, dmg));
            return dmg;
        }

        private int ResolveSkillBaseStat(Skill skill) =>
            skill.StatToScaleFrom.ToUpper() switch
            {
                "ATK"  => Character.TotalPhysicalAttack,
                "MATK" => Character.TotalMagicAttack,
                "SPR"  => Character.TotalSPR,
                "INT"  => Character.TotalINT,
                "DEX"  => Character.TotalDEX,
                "AIM"  => Character.TotalAim * 2,
                "EVA"  => Character.TotalEvasion * 2,
                "END"  => Character.TotalEND,
                "STR"  => Character.TotalSTR,
                _      => Character.TotalPhysicalAttack
            };

        // Uses the caster's PRIMARY stat for effect magnitude scaling.
        // ATK/MATK map to their underlying stat (STR/INT) so all skills
        // start at a similar baseline (~8-10 at level 1).
        private int ResolveEffectStat(Skill skill) =>
            skill.StatToScaleFrom.ToUpper() switch
            {
                "ATK"             => Character.TotalSTR,
                "MATK"            => Character.TotalINT,
                "SPR"             => Character.TotalSPR,
                "INT"             => Character.TotalINT,
                "DEX" or "AIM" or "EVA" => Character.TotalDEX,
                "END"             => Character.TotalEND,
                "STR"             => Character.TotalSTR,
                _                 => Character.TotalSTR
            };

        /// <summary>Decrements every active per-skill cooldown by one turn, removing any that
        /// reach zero. Called exactly once per completed player action (see ExecuteSkill's remark
        /// on why cooldowns are stored as Cooldown+1) - NOT from Tick(), since GameHub's multiplayer
        /// combat path never calls Tick() at all (only Myria.Console's combat loop does), so
        /// anything relying on Tick() to advance would never decrement there.</summary>
        private void TickSkillCooldowns()
        {
            if (_skillCooldowns.Count == 0) return;
            foreach (var key in _skillCooldowns.Keys.ToList())
            {
                if (--_skillCooldowns[key] <= 0)
                    _skillCooldowns.Remove(key);
            }
        }

        private void EndCharacterAction()
        {
            TickSkillCooldowns();

            if (!Enemy.IsAlive)
            {
                FinishCharacterWon();
                return;
            }

            if (RecoveryTurnsRemaining > 0)
            {
                Phase = CombatPhase.Recovery;
                EnemyTurn();
                return;
            }

            EnemyTurn();
        }

        private void EnemyTurn()
        {
            if (!Enemy.IsAlive) { FinishCharacterWon(); return; }
            if (!Character.IsAlive) { FinishCharacterLost(); return; }

            bool enemyStunned = EffectProcessor.IsStunned(Enemy);
            foreach (var entry in EffectProcessor.Tick(Enemy))
                Log.Add(entry);

            if (!Enemy.IsAlive) { FinishCharacterWon(); return; }

            if (enemyStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", Enemy.Name));
                Phase = (RecoveryTurnsRemaining > 0) ? CombatPhase.Recovery : CombatPhase.CharacterTurn;
                return;
            }

            int dmg = CombatSystem.CalculateDamage(Enemy, Character);
            if (dmg <= 0)
                Log.Add(new CombatLogEntry("pg.fight.log.enemyMiss", Enemy.Name));
            else
            {
                Character.ApplyDamage(dmg);
                Log.Add(new CombatLogEntry("pg.fight.log.enemyHit", Enemy.Name, dmg));
            }

            if (!Character.IsAlive)
            {
                // Solo encounters have no allies, so resurrection cannot trigger for the player.
                FinishCharacterLost();
            }
            else
            {
                Phase = (RecoveryTurnsRemaining > 0) ? CombatPhase.Recovery : CombatPhase.CharacterTurn;
            }
        }
        public Dictionary<string, int> GetDropNames()
        {
            return _dropnumbers;
        }
        private static long ScaleXp(long baseXp, int playerLevel, int monsterLevel)
        {
            if (playerLevel <= monsterLevel) return baseXp;
            double ratio = (double)monsterLevel / playerLevel;
            return Math.Max(1L, (long)(baseXp * ratio * ratio));
        }

        private void FinishCharacterWon()
        {
            Phase = CombatPhase.Finished;
            long xpGained = ScaleXp(Enemy.Exp, Character.Level, Enemy.Level);
            LastXpGained = xpGained;
            Character.GainXp(xpGained);
            ClassManager.GrantClassXp(Character, xpGained);
            SkillFactory.UpdateSkills(Character);

            MonsterKilled?.Invoke(this, new MonsterKilledEventArgs(Enemy.Id));
            GameEvents.FireMonsterKilled(Character, Enemy);

            _drops = LootGenerator.GetLootFor(Enemy);
            _dropnumbers = new();
            foreach (var drop in _drops)
            {
                _dropnumbers.Add(drop.Name, drop.StackSize);
            }
            foreach (var drop in _drops)
            {
                if (drop.StackSize == 0)
                    drop.StackSize = 1;
                if (!Character.Inventory.AddItem(drop, Character))
                    InventoryFull = true;
            }
            if (Character.CurrentRoom.IsDungeonRoom)
            {
                Character.CurrentRoom.CurrentMonsters.Remove(Enemy);
                if (Character.CurrentRoom.CurrentMonsters.Count == 0)
                    Character.CurrentRoom.IsCleared = true;
            }

            DayCycleManager.AddTicks(GameTick.CombatVictory);
            Log.Add(new CombatLogEntry("pg.fight.log.win", Enemy.Name));
        }

        private void FinishCharacterLost()
        {
            Phase = CombatPhase.Finished;
            Log.Add(new CombatLogEntry("pg.fight.log.lose"));
        }

    }

}
