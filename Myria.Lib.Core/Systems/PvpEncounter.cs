using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Systems
{
    /// <summary>
    /// Party-vs-party (or 1v1) PvP fight. Deliberately its own class rather than a generalised
    /// GroupCombatEncounter: that one has monster XP/loot/quest/dungeon resolution woven through its
    /// win path, while PvP's only stakes are rating + PvP currency (see PvpRatingService), applied by
    /// the caller. The combat math underneath is the same (CombatSystem, EffectProcessor, the skill
    /// damage/heal formulas).
    /// <para>
    /// Turn order interleaves both sides by DEX (initiative roll, like group combat; no bonus
    /// turns). Sides are fixed lists for the whole fight - a character who flees stays in their
    /// list, just out of the fight - so the opponent/ally indices clients send never shift.
    /// </para>
    /// <para>
    /// Thread-safe: unlike a party fighting monsters, the participants here are adversaries acting
    /// from different connections, and a stalled turn can be skipped by anyone, so every public
    /// mutator runs under one lock.
    /// </para>
    /// </summary>
    public sealed class PvpEncounter
    {
        /// <summary>How long the current participant has to act before anyone may skip their turn.</summary>
        public static TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(30);

        private readonly object _sync = new();
        private readonly List<Character> _order;                 // initiative order, both sides
        private readonly HashSet<Character> _fled = new();
        private readonly Dictionary<Character, (int Hp, int Mp)> _preFight;
        private readonly Dictionary<Character, Dictionary<string, int>> _skillCooldowns = new();
        private int _turnIndex;
        private readonly Func<DateTime> _clock;

        public IReadOnlyList<Character> SideA { get; }
        public IReadOnlyList<Character> SideB { get; }
        public List<CombatLogEntry> Log { get; } = new();

        public bool IsFinished { get; private set; }
        /// <summary>1 = SideA won, 2 = SideB won, 0 = not finished.</summary>
        public int WinningSide { get; private set; }
        public DateTime TurnStartedUtc { get; private set; }

        public PvpEncounter(IEnumerable<Character> sideA, IEnumerable<Character> sideB, Func<DateTime>? clock = null)
        {
            SideA = sideA.ToList();
            SideB = sideB.ToList();
            if (SideA.Count == 0 || SideB.Count == 0)
                throw new ArgumentException("Both sides need at least one character.");
            if (SideA.Intersect(SideB).Any())
                throw new ArgumentException("A character can't be on both sides.");

            _clock = clock ?? (() => DateTime.UtcNow);
            var all = SideA.Concat(SideB).ToList();
            _preFight = all.ToDictionary(c => c, c => (c.CurrentHealth, c.CurrentMana));

            var rng = Random.Shared;
            _order = all.Select(c => (c, tie: rng.Next()))
                        .OrderByDescending(x => x.c.TotalDEX).ThenBy(x => x.tie)
                        .Select(x => x.c).ToList();

            Log.Add(new CombatLogEntry("pg.fight.pvp.start",
                string.Join(", ", SideA.Select(c => c.Name)), string.Join(", ", SideB.Select(c => c.Name))));
            _turnIndex = -1;
            AdvanceTurn();
        }

        // ── Queries ───────────────────────────────────────────────────────────

        public string CurrentTurnCharacterName
        {
            get { lock (_sync) return IsFinished || _turnIndex < 0 ? "" : _order[_turnIndex].Name; }
        }

        public bool IsInFight(Character c) => !_fled.Contains(c) && c.IsAlive;
        public bool HasFled(Character c) => _fled.Contains(c);

        /// <summary>1 or 2 for a participant (including one who fled), else 0.</summary>
        public int SideOf(string name) =>
            SideA.Any(c => Is(c, name)) ? 1 : SideB.Any(c => Is(c, name)) ? 2 : 0;

        public Character? Find(string name) => _order.FirstOrDefault(c => Is(c, name));

        /// <summary>The opposing side's list (fixed order - what opponent target indices refer to).</summary>
        public IReadOnlyList<Character> OpponentsOf(Character c) => SideA.Contains(c) ? SideB : SideA;
        /// <summary>The character's own side's list (what ally target indices refer to).</summary>
        public IReadOnlyList<Character> AlliesOf(Character c) => SideA.Contains(c) ? SideA : SideB;

        public IReadOnlyList<Character> Winners => WinningSide switch
        {
            1 => SideA.Where(c => !_fled.Contains(c)).ToList(),
            2 => SideB.Where(c => !_fled.Contains(c)).ToList(),
            _ => Array.Empty<Character>(),
        };

        public IReadOnlyList<Character> Losers => WinningSide switch
        {
            1 => SideB.Where(c => !_fled.Contains(c)).ToList(),
            2 => SideA.Where(c => !_fled.Contains(c)).ToList(),
            _ => Array.Empty<Character>(),
        };

        // ── Actions ───────────────────────────────────────────────────────────

        public bool Attack(string actorName, int opponentIndex)
        {
            lock (_sync)
            {
                if (!BeginTurn(actorName, out var actor)) return false;
                var opponents = OpponentsOf(actor);
                if (opponentIndex < 0 || opponentIndex >= opponents.Count || !IsInFight(opponents[opponentIndex]))
                    return false;
                if (!TickStartOfTurn(actor)) return true; // stunned/killed - turn spent

                var target = opponents[opponentIndex];
                var (dmg, isCrit) = CombatSystem.CalculateDamageWithCrit(actor, target);
                if (dmg <= 0)
                    Log.Add(new CombatLogEntry("pg.fight.log.miss", actor.Name));
                else
                {
                    target.TakeDamage(dmg);
                    Log.Add(isCrit
                        ? new CombatLogEntry("pg.fight.log.critHit", actor.Name, dmg)
                        : new CombatLogEntry("pg.fight.log.hit", actor.Name, dmg));
                    HandleDowned(target);
                }
                EndAction(actor);
                return true;
            }
        }

        /// <summary><paramref name="targetIndex"/> indexes the opponents for enemy-targeted skills,
        /// the caster's own side for SingleAlly, and is ignored otherwise.</summary>
        public bool CastSkill(string actorName, Skill skill, int targetIndex)
        {
            lock (_sync)
            {
                if (!BeginTurn(actorName, out var caster)) return false;
                var opponents = OpponentsOf(caster);
                var allies    = AlliesOf(caster);

                Character? single = skill.Target switch
                {
                    SkillTarget.SingleEnemy => targetIndex >= 0 && targetIndex < opponents.Count ? opponents[targetIndex] : null,
                    SkillTarget.SingleAlly  => targetIndex >= 0 && targetIndex < allies.Count ? allies[targetIndex] : caster,
                    _ => caster,
                };
                if (single == null || !IsInFight(single)) return false;
                if (EffectProcessor.IsSilenced(caster))
                {
                    Log.Add(new CombatLogEntry("pg.fight.effect.silenced", caster.Name));
                    return false;
                }
                if (caster.CurrentMana < skill.ManaCost)
                {
                    Log.Add(new CombatLogEntry("pg.fight.log.nomana"));
                    return false;
                }
                int cd = _skillCooldowns.TryGetValue(caster, out var cds) && cds.TryGetValue(skill.Id, out var v) ? v : 0;
                if (cd > 0)
                {
                    Log.Add(new CombatLogEntry("pg.fight.log.onCooldown", skill.Name, cd));
                    return false;
                }
                if (!TickStartOfTurn(caster)) return true;

                caster.SpendMana(skill.ManaCost);
                if (skill.Cooldown > 0)
                {
                    if (!_skillCooldowns.TryGetValue(caster, out var casterCds)) _skillCooldowns[caster] = casterCds = new();
                    casterCds[skill.Id] = skill.Cooldown + 1; // same +1 storage offset as the PvE encounters
                }
                SkillLevelingService.GrantUsage(caster, skill.Id);

                List<Character> primary = skill.Target switch
                {
                    SkillTarget.AllEnemies => opponents.Where(IsInFight).ToList(),
                    SkillTarget.AllAllies  => allies.Where(IsInFight).ToList(),
                    _ => new List<Character> { single },
                };

                bool offensive = skill.Target is SkillTarget.SingleEnemy or SkillTarget.AllEnemies;
                int totalDamage = 0;
                foreach (var t in primary)
                {
                    if (offensive) { totalDamage += SkillDamage(skill, caster, t); HandleDowned(t); }
                    else SkillHeal(skill, caster, t);
                    if (IsFinished) break;
                }

                ApplySkillEffects(skill, caster, primary, allies, totalDamage);
                if (!IsFinished) EndAction(caster);
                return true;
            }
        }

        public bool UseItem(string actorName, ConsumableItem item)
        {
            lock (_sync)
            {
                if (!BeginTurn(actorName, out var actor)) return false;
                if (!actor.Inventory.Items.Contains(item)) return false;
                if (!TickStartOfTurn(actor)) return true;

                item.Use(actor);
                actor.Inventory.ConsumeOne(item);
                Log.Add(new CombatLogEntry("pg.fight.log.usedItem", actor.Name, item.Name));
                EndAction(actor);
                return true;
            }
        }

        /// <summary>Forfeit: the character leaves the fight (still listed, so indices stay stable).
        /// If that leaves their side with nobody fighting, the other side wins.</summary>
        public bool Flee(string name)
        {
            lock (_sync)
            {
                var c = Find(name);
                if (c == null || IsFinished || _fled.Contains(c)) return false;
                bool wasTheirTurn = _turnIndex >= 0 && _order[_turnIndex] == c;
                _fled.Add(c);
                Log.Add(new CombatLogEntry("pg.fight.pvp.fled", c.Name));
                CheckFinished();
                if (!IsFinished && wasTheirTurn) AdvanceTurn();
                return true;
            }
        }

        /// <summary>Skips the current turn if it has run past <see cref="TurnTimeout"/>. Anyone may
        /// call it (there's no accept/decline, so a fight against an AFK player would otherwise
        /// stall forever); returns whether a turn was skipped.</summary>
        public bool SkipStalledTurn()
        {
            lock (_sync)
            {
                if (IsFinished || _turnIndex < 0 || _clock() - TurnStartedUtc < TurnTimeout) return false;
                Log.Add(new CombatLogEntry("pg.fight.pvp.turnSkipped", _order[_turnIndex].Name));
                AdvanceTurn();
                return true;
            }
        }

        /// <summary>
        /// Puts a participant back exactly as they entered the fight - HP/MP restored and every
        /// fight effect (buffs, debuffs, poison, stun) cleared - so PvP never changes anything about
        /// normal play (v0.3 decision). Call for each participant once they're out of the fight.
        /// Items used stay used.
        /// </summary>
        public void RestorePreFightState(Character c)
        {
            lock (_sync)
            {
                if (!_preFight.TryGetValue(c, out var pre)) return;
                c.ActiveEffects.Clear();
                c.SetHealth(Math.Max(1, pre.Hp));
                c.SetMana(pre.Mp);
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static bool Is(Character c, string name) =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase);

        private bool BeginTurn(string actorName, out Character actor)
        {
            actor = null!;
            if (IsFinished || _turnIndex < 0) return false;
            var current = _order[_turnIndex];
            if (!Is(current, actorName)) return false;
            actor = current;
            return true;
        }

        /// <summary>Start-of-turn effect ticks, like the PvE encounters. Returns false if the
        /// actor can't act (stunned, or killed by a DoT) - the turn is then already spent.</summary>
        private bool TickStartOfTurn(Character actor)
        {
            bool stunned = EffectProcessor.IsStunned(actor);
            foreach (var entry in EffectProcessor.Tick(actor))
                Log.Add(entry);

            if (!actor.IsAlive)
            {
                HandleDowned(actor);
                if (!IsFinished) AdvanceTurn();
                return false;
            }
            if (stunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", actor.Name));
                EndAction(actor);
                return false;
            }
            return true;
        }

        private int SkillDamage(Skill skill, Character caster, Character target)
        {
            if (skill.IsHealing) return 0;
            float raw = ResolveBaseStat(skill, caster) * skill.ScalingFactor;
            float def = skill.Type == SkillType.Physical ? target.TotalPhysicalDefense : target.TotalMagicDefense;
            int dmg = Math.Max(1, (int)(raw * (raw / (raw + def))));   // same formula as group combat
            target.TakeDamage(dmg);
            Log.Add(new CombatLogEntry("pg.fight.log.skillHit", caster.Name, skill.Name, dmg));
            return dmg;
        }

        private void SkillHeal(Skill skill, Character caster, Character target)
        {
            if (LastingBuff.From(skill, caster.Name) is { } lasting)
            {
                target.ApplyLastingBuff(lasting);
                Log.Add(new CombatLogEntry("pg.fight.log.lastingBuff", caster.Name, target.Name, skill.Name, skill.LastingBuff!.DurationMinutes));
            }
            if (!skill.IsHealing) return;
            int heal = CombatEntity.ScaleHeal((int)(ResolveBaseStat(skill, caster) * skill.ScalingFactor));
            int healed = Math.Min(heal, target.MaxHealth - target.CurrentHealth);
            target.Heal(healed);
            Log.Add(new CombatLogEntry("pg.fight.log.heal", caster.Name, healed));
        }

        private void ApplySkillEffects(Skill skill, Character caster, List<Character> primary, IReadOnlyList<Character> allies, int totalDamage)
        {
            int effectStat = ResolveEffectStat(skill, caster);
            foreach (var entry in skill.Effects)
            {
                // v0.3: lifesteal / mana siphon heal the caster once from the skill's total damage, not once
                // per host (an area skill used to heal once per enemy hit).
                var drain = EffectFactory.CreateEffect(entry.EffectId, skill.Id, skill.ScalingFactor, effectStat);
                if (drain == null) continue;
                if (drain.Type == EffectType.LifeSteal)
                {
                    int amount = Math.Max(1, (int)(totalDamage * drain.Magnitude));
                    caster.Heal(amount);
                    Log.Add(new CombatLogEntry("pg.fight.effect.lifesteal", caster.Name, amount));
                    continue;
                }
                if (drain.Type == EffectType.ManaSiphon)
                {
                    int amount = Math.Max(1, (int)(totalDamage * drain.Magnitude));
                    caster.RestoreMana(amount);
                    Log.Add(new CombatLogEntry("pg.fight.effect.manasiphon", caster.Name, amount));
                    continue;
                }

                var hosts = entry.ApplyTo switch
                {
                    EffectTarget.Caster    => new List<Character> { caster },
                    EffectTarget.AllAllies => allies.Where(IsInFight).ToList(),
                    _                      => primary.Where(IsInFight).ToList(),
                };
                foreach (var host in hosts)
                {
                    var effect = EffectFactory.CreateEffect(entry.EffectId, skill.Id, skill.ScalingFactor, effectStat);
                    if (effect == null) continue;
                    effect.SourceCharacterName = caster.Name;

                    EffectProcessor.Apply(host, effect);
                    Log.Add(new CombatLogEntry("pg.fight.effect.applied", effect.Name, host.Name));
                }
            }
        }

        private void HandleDowned(Character c)
        {
            if (c.IsAlive) return;
            bool hasAllies = AlliesOf(c).Any(a => a != c && IsInFight(a));
            if (EffectProcessor.TryResurrect(c, hasAllies))
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.resurrected", c.Name));
                return;
            }
            Log.Add(new CombatLogEntry("pg.fight.pvp.defeated", c.Name));
            CheckFinished();
        }

        private void CheckFinished()
        {
            if (IsFinished) return;
            bool aIn = SideA.Any(IsInFight), bIn = SideB.Any(IsInFight);
            if (aIn && bIn) return;
            IsFinished = true;
            WinningSide = aIn ? 1 : 2;
            var winners = WinningSide == 1 ? SideA : SideB;
            Log.Add(new CombatLogEntry("pg.fight.pvp.won", string.Join(", ", winners.Where(c => !_fled.Contains(c)).Select(c => c.Name))));
        }

        private void EndAction(Character actor)
        {
            if (_skillCooldowns.TryGetValue(actor, out var cds))
                foreach (var key in cds.Keys.ToList())
                    if (--cds[key] <= 0) cds.Remove(key);
            if (!IsFinished) AdvanceTurn();
        }

        private void AdvanceTurn()
        {
            for (int step = 1; step <= _order.Count; step++)
            {
                int idx = (_turnIndex + step) % _order.Count;
                if (IsInFight(_order[idx]))
                {
                    _turnIndex = idx;
                    TurnStartedUtc = _clock();
                    return;
                }
            }
            CheckFinished(); // nobody left who can act
        }

        private static int ResolveBaseStat(Skill skill, Character c) => CombatSystem.SkillBaseStat(c, skill);

        private static int ResolveEffectStat(Skill skill, Character c) => CombatSystem.SkillEffectStat(c, skill);
    }
}
