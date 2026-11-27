using Myria.Lib.Core.Entities;
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
    public sealed class GroupCombatEncounter
    {
        // Not readonly, deliberately - see AddCharacter's copy-on-write comment. Every other
        // action method (CharacterAttack, CharacterCastSkill, HandleMonsterDeath, etc.) reads
        // this field only via the Characters property below, evaluated fresh at the start of
        // each foreach/LINQ call.
        private List<Character> _characters;
        public IReadOnlyList<Character>  Characters  => _characters;
        public List<Monster>          Monsters { get; }

        public bool IsFinished  { get; private set; }
        public bool CharactersWon  { get; private set; }
        public List<CombatLogEntry> Log { get; } = new();

        private int _currentCharacterIndex;

        // Per-character, per-skill cooldowns for this fight only - reset every new encounter
        // (never persisted on Character). Keyed per-character (unlike solo CombatEncounter, which
        // only ever has one character) since two party members can independently be on/off
        // cooldown for the same skill id. Stored as Cooldown+1, see CombatEncounter's matching
        // field for why - decremented once per completed action, for the acting character only,
        // in AdvanceAfterCharacterAction (this encounter has no Tick()/multi-turn casting at all,
        // every action resolves synchronously). That +1 offset is the ONLY compensation needed for
        // the guaranteed same-turn decrement - GetSkillCooldownRemaining must return the raw stored
        // value as-is; subtracting again here double-compensates and cuts every skill's effective
        // cooldown one turn short of what was authored.
        private readonly Dictionary<Character, Dictionary<string, int>> _skillCooldowns = new();

        private int GetSkillCooldownRemaining(Character character, string skillId) =>
            _skillCooldowns.TryGetValue(character, out var cds) && cds.TryGetValue(skillId, out var v)
                ? v : 0;

        // ── Turn timeline (v0.3) ────────────────────────────────────────────
        // Players and monsters share one timeline: everyone's next turn has a time, and whoever is
        // next acts. Acting pushes your next turn back by your delay, which shrinks with DEX - a
        // fast combatant's turns come round more often and slip past slower ones in the chain, so
        // over time they get extra moves, but never two in a row out of nowhere. A player's speed is
        // measured against the party's average DEX on a softened curve (capped); monsters act at
        // normal speed - their DEX was tuned for hit/dodge, and counting it made every monster act
        // up to twice per player turn. So within a party the fast act more and the slow less, while
        // the party as a whole keeps its pace against the monsters.
        // Replaces the DEX bonus-action gauge, which was measured against the party alone: every
        // player got a bonus action every 2-3 turns whatever the monsters' speed (monsters never
        // did), players of similar DEX all got theirs in the same round, and a Rogue well above the
        // party's average got one nearly every turn.
        private readonly Dictionary<CombatEntity, double> _nextTurnAt = new(ReferenceEqualityComparer.Instance);
        private double _now;
        private readonly double _referenceSpeed;
        public const double BaseTurnDelay = 100.0;

        /// <summary>How strongly DEX speeds turns up: turns per time = (DEX / fight average) ^ this,
        /// clamped to [MinSpeedFactor, MaxSpeedFactor]. 0.5: twice the average DEX = ~1.4x the turns.
        /// (Linear made monsters, whose DEX was tuned for hit/dodge, act twice per player turn.)</summary>
        public static double SpeedExponent { get; set; } = 0.5;
        public static double MinSpeedFactor { get; set; } = 0.67;
        public static double MaxSpeedFactor { get; set; } = 1.5;

        /// <summary>Monsters always act at normal speed and players' speed is measured against the
        /// party's own average DEX (monster DEX was tuned for hit/dodge, not turn speed).</summary>
        public static bool MonstersAtNormalSpeed { get; set; } = true;

        /// <summary>Time between two turns of <paramref name="entity"/>: BaseTurnDelay for an
        /// average-DEX combatant, shorter for a faster one (see <see cref="SpeedExponent"/>).</summary>
        private double TurnDelay(CombatEntity entity)
        {
            if (MonstersAtNormalSpeed && entity is Monster) return BaseTurnDelay;
            double ratio = Math.Max(0.01, entity.TotalDEX / _referenceSpeed);
            double factor = Math.Clamp(Math.Pow(ratio, SpeedExponent), MinSpeedFactor, MaxSpeedFactor);
            return BaseTurnDelay / factor;
        }

        public string CurrentTurnCharacterName =>
            IsFinished || _currentCharacterIndex >= Characters.Count
                ? ""
                : Characters[_currentCharacterIndex].Name;

        public event EventHandler<MonsterKilledEventArgs>? MonsterKilled;

        /// <summary>
        /// Total actual (level-scaled) XP granted to each character across this encounter so far,
        /// accumulated per kill. Read this instead of summing Monsters' raw Exp values for
        /// anything XP-derived (UI display, bonus calculations) — each character can be scaled
        /// differently per kill depending on their own level vs. that monster's level.
        /// </summary>
        public Dictionary<Character, long> XpGrantedByCharacter { get; } = new();

        /// <summary>Item ids granted to each character across this encounter so far (accumulated
        /// per kill, one entry per dropped item). Only the single living recipient of a given kill's
        /// loot gets entries added for that kill.</summary>
        public Dictionary<Character, List<string>> LootGrantedByCharacter { get; } = new();

        public GroupCombatEncounter(IEnumerable<Character> characters, IEnumerable<Monster> enemies)
        {
            var rng = Random.Shared;
            _characters = characters
                .Select(p => (character: p, tieBreak: rng.Next()))
                .OrderByDescending(x => x.character.TotalDEX)
                .ThenBy(x => x.tieBreak)
                .Select(x => x.character)
                .ToList();

            Monsters = enemies.Select(m => { m.ResetHealth(); return m; }).ToList();

            // Reset aggro for all participants at encounter start.
            foreach (var c in Characters)
                c.AggroLevel = 0f;

            // Everyone's first turn comes within their first delay; players get the earlier window,
            // so a fight usually opens with them, but a fast monster can still strike first.
            var all = Characters.Cast<CombatEntity>().Concat(Monsters).ToList();
            _referenceSpeed = Math.Max(1.0, MonstersAtNormalSpeed
                ? Characters.Average(c => (double)c.TotalDEX)
                : all.Average(e => (double)e.TotalDEX));
            foreach (var c in Characters) _nextTurnAt[c] = TurnDelay(c) * (0.1 + 0.6 * rng.NextDouble());
            foreach (var m in Monsters)   _nextTurnAt[m] = TurnDelay(m) * (0.4 + 0.6 * rng.NextDouble());

            Log.Add(new CombatLogEntry("pg.fight.log.start",
                string.Join(", ", Monsters.Select(m => m.Name))));
            Log.Add(new CombatLogEntry("pg.fight.log.order",
                string.Join(", ", Characters.Select(p => p.Name))));
            AdvanceTimeline();
        }

        // Guards only AddCharacter's own check-then-write (two late joiners arriving in the same
        // instant could otherwise both pass the "not already in" check before either writes,
        // double-adding the same character) - not a general encounter-wide lock. See the
        // copy-on-write comment below for why the actual list mutation doesn't need one.
        private readonly object _joinLock = new();

        /// <summary>
        /// Adds a late-arriving party member to an already-running encounter (a player who steps
        /// into the room after their party's fight there has already started, or who presses
        /// "Start Group Fight" themselves while that fight is still going). Appended to the end of
        /// the turn order rather than reshuffled in, so nobody else's already-decided turn order
        /// shifts around mid-fight; they simply get their first turn once play reaches the end of
        /// the (now longer) roster. Their first turn comes one full turn delay from now on the
        /// timeline; the fight's reference speed isn't recalculated, so nobody else's pacing changes.
        ///
        /// GameHub can call this (from a joining player's connection) fully concurrently with
        /// CharacterAttack/CharacterCastSkill (from an already-fighting player's connection) on
        /// this same encounter instance - nothing serializes hub calls across different SignalR
        /// connections. The old in-place `_characters.Add(...)` mutated the exact List<Character>
        /// another thread could be mid-`foreach` over in HandleMonsterDeath at that moment (e.g. a
        /// party member joining right as another lands the killing blow), which either throws
        /// InvalidOperationException ("Collection was modified") or corrupts the enumeration -
        /// see the 2026-09-10 security audit. Replacing the list wholesale instead of mutating it
        /// in place fixes this without needing a lock around every read site in the file: a
        /// `foreach` captures its enumerator once at the start of the loop, so an in-progress
        /// enumeration keeps safely iterating the *old* list object even after this method
        /// swaps `_characters` to point at a new one containing the joiner. The next read of the
        /// `Characters` property anywhere else in the encounter simply sees the new list.
        /// </summary>
        public void AddCharacter(Character character)
        {
            lock (_joinLock)
            {
                if (IsFinished || _characters.Any(c => c == character)) return;

                character.AggroLevel = 0f;
                _nextTurnAt[character] = _now + TurnDelay(character);   // first turn after a full delay
                _characters = new List<Character>(_characters) { character };
                Log.Add(new CombatLogEntry("pg.fight.log.joined", character.Name));
            }
        }

        /// <summary>
        /// Removes a party member who flees mid-fight (multiplayer "Run") from this encounter,
        /// without ending it for whoever's left - mirrors <see cref="AddCharacter"/>'s late-join
        /// shape in reverse. Fixes a real bug (2026-09-10, TODO.md item 51): the flee handler
        /// used to only unregister the fleeing player's own SignalR connection from the fight
        /// registry, never touching the shared encounter's own turn order/roster at all - if it
        /// was (or became) the fled character's turn, nobody could ever act for them again, and
        /// the remaining party member's own actions kept getting silently rejected
        /// (IsThisCharactersTurn never matched them) with their Attack button stuck disabled.
        /// <para>
        /// Nobody else's already-decided turn order shifts because of this - the fled character
        /// simply drops out of it. If it was (or had just become, mid-removal) their turn, play
        /// advances to the next living character exactly as if they'd finished a do-nothing
        /// action. If the last remaining character flees, the fight ends as a loss (mirrors
        /// <see cref="AdvanceTimeline"/>/<see cref="MonsterTurn"/>'s "nobody left standing"
        /// handling) - there's nobody left to keep it going, and nobody left to see the result
        /// either way.
        /// </para>
        /// </summary>
        public void RemoveCharacter(string characterName)
        {
            lock (_joinLock)
            {
                if (IsFinished) return;

                int idx = _characters.FindIndex(c => string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return;

                bool wasCurrentTurn = idx == _currentCharacterIndex;
                var fled = _characters[idx];

                var newList = new List<Character>(_characters);
                newList.RemoveAt(idx);
                _characters = newList;
                Log.Add(new CombatLogEntry("pg.fight.log.fled", fled.Name));
                _nextTurnAt.Remove(fled);

                if (_characters.Count == 0) { FinishCharactersLost(); return; }

                if (idx < _currentCharacterIndex)
                {
                    // An earlier slot was removed - shift down so the index still points at the
                    // same character whose turn was already correctly in progress.
                    _currentCharacterIndex--;
                    return;
                }

                if (!wasCurrentTurn) return; // someone else's turn: nothing to adjust

                // The character who just left was up - play moves on along the timeline.
                AdvanceTimeline();
            }
        }

        // ── Actions ───────────────────────────────────────────────────────────

        public bool CharacterAttack(string playerName, int targetMonsterIndex)
        {
            if (IsFinished || !IsThisCharactersTurn(playerName)) return false;

            var attacker = Characters[_currentCharacterIndex];

            bool attackerStunned = EffectProcessor.IsStunned(attacker);
            foreach (var entry in EffectProcessor.Tick(attacker))
                Log.Add(entry);

            if (!attacker.IsAlive)
            {
                AdvanceAfterCharacterAction();
                return false;
            }

            if (attackerStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", attacker.Name));
                AdvanceAfterCharacterAction();
                return true;
            }

            var target = GetMonster(targetMonsterIndex);
            if (target == null || !target.IsAlive) return false;

            attacker.AggroLevel += 1f;

            var (dmg, isCrit) = CombatSystem.CalculateDamageWithCrit(attacker, target);
            if (dmg <= 0)
                Log.Add(new CombatLogEntry("pg.fight.log.miss", attacker.Name));
            else
            {
                target.TakeDamage(dmg);
                Log.Add(isCrit
                    ? new CombatLogEntry("pg.fight.log.critHit", attacker.Name, dmg)
                    : new CombatLogEntry("pg.fight.log.hit", attacker.Name, dmg));
                HandleMonsterDeath(target);
            }

            if (!IsFinished) AdvanceAfterCharacterAction();
            return true;
        }

        public bool CharacterCastSkill(string playerName, Skill skill, int targetIndex)
        {
            if (IsFinished || !IsThisCharactersTurn(playerName)) return false;

            var caster = Characters[_currentCharacterIndex];

            bool casterStunned = EffectProcessor.IsStunned(caster);
            foreach (var entry in EffectProcessor.Tick(caster))
                Log.Add(entry);

            if (!caster.IsAlive)
            {
                AdvanceAfterCharacterAction();
                return false;
            }

            if (casterStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", caster.Name));
                AdvanceAfterCharacterAction();
                return true;
            }

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

            int cooldownRemaining = GetSkillCooldownRemaining(caster, skill.Id);
            if (cooldownRemaining > 0)
            {
                Log.Add(new CombatLogEntry("pg.fight.log.onCooldown", skill.Name, cooldownRemaining));
                return false;
            }

            caster.SpendMana(skill.ManaCost);
            if (skill.Cooldown > 0)
            {
                if (!_skillCooldowns.TryGetValue(caster, out var casterCooldowns))
                    _skillCooldowns[caster] = casterCooldowns = new();
                casterCooldowns[skill.Id] = skill.Cooldown + 1;
            }
            SkillLevelingService.GrantUsage(caster, skill.Id);
            caster.AggroLevel += 1f + skill.AggroModifier;

            // Resolve primary effect targets based on skill targeting.
            List<CombatEntity> primaryTargets = skill.Target switch
            {
                SkillTarget.AllEnemies  => Monsters.Where(m => m.IsAlive).Cast<CombatEntity>().ToList(),
                SkillTarget.SingleEnemy => GetMonster(targetIndex) is { IsAlive: true } m
                                              ? new List<CombatEntity> { m }
                                              : new List<CombatEntity>(),
                SkillTarget.SingleAlly  => GetCharacter(targetIndex) is { IsAlive: true } a
                                              ? new List<CombatEntity> { a }
                                              : new List<CombatEntity> { caster },
                SkillTarget.AllAllies   => Characters.Where(c => c.IsAlive).Cast<CombatEntity>().ToList(),
                _                       => new List<CombatEntity> { caster }, // Self
            };

            int totalDamageForLifesteal = 0;

            switch (skill.Target)
            {
                case SkillTarget.Self:
                    ExecuteSkillOnCharacter(skill, caster, caster);
                    break;

                case SkillTarget.SingleAlly:
                    var ally = GetCharacter(targetIndex);
                    if (ally == null || !ally.IsAlive) { caster.SpendMana(-skill.ManaCost); return false; }
                    ExecuteSkillOnCharacter(skill, caster, ally);
                    break;

                case SkillTarget.AllAllies:
                    foreach (var member in Characters.Where(c => c.IsAlive).ToList())
                        ExecuteSkillOnCharacter(skill, caster, member);
                    break;

                case SkillTarget.SingleEnemy:
                    var enemy = GetMonster(targetIndex);
                    if (enemy == null || !enemy.IsAlive) { caster.SpendMana(-skill.ManaCost); return false; }
                    totalDamageForLifesteal += ExecuteSkillOnMonster(skill, caster, enemy);
                    HandleMonsterDeath(enemy);
                    break;

                case SkillTarget.AllEnemies:
                    foreach (var m in Monsters.Where(m => m.IsAlive).ToList())
                    {
                        totalDamageForLifesteal += ExecuteSkillOnMonster(skill, caster, m);
                        HandleMonsterDeath(m);
                        if (IsFinished) break;
                    }
                    break;

                default:
                    // Unrecognized/mod-added Target: the resolution switch above already treats
                    // this as Self (see primaryTargets), so execution matches that instead of
                    // silently doing nothing (mana already spent) as it would have before.
                    ExecuteSkillOnCharacter(skill, caster, caster);
                    break;
            }

            // Apply data-driven effects to resolved targets.
            int effectStat = ResolveEffectStat(skill, caster);
            foreach (var entry in skill.Effects)
            {
                // v0.3: lifesteal / mana siphon heal the caster once from the skill's total damage, not once
                // per host (an area skill used to heal once per enemy hit).
                var drain = EffectFactory.CreateEffect(entry.EffectId, skill.Id, skill.ScalingFactor, effectStat);
                if (drain == null) continue;
                if (drain.Type == EffectType.LifeSteal)
                {
                    int amount = Math.Max(1, (int)(totalDamageForLifesteal * drain.Magnitude));
                    caster.Heal(amount);
                    Log.Add(new CombatLogEntry("pg.fight.effect.lifesteal", caster.Name, amount));
                    continue;
                }
                if (drain.Type == EffectType.ManaSiphon)
                {
                    int amount = Math.Max(1, (int)(totalDamageForLifesteal * drain.Magnitude));
                    caster.RestoreMana(amount);
                    Log.Add(new CombatLogEntry("pg.fight.effect.manasiphon", caster.Name, amount));
                    continue;
                }

                var hosts = entry.ApplyTo switch
                {
                    EffectTarget.Caster    => new List<CombatEntity> { caster },
                    EffectTarget.AllAllies => Characters.Where(c => c.IsAlive).Cast<CombatEntity>().ToList(),
                    _                      => primaryTargets
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

            skill.Effect?.Invoke(caster, GetMonster(targetIndex) ?? (ICombatant)caster);

            if (!IsFinished) AdvanceAfterCharacterAction();
            return true;
        }

        /// <summary>
        /// Uses a consumable from the acting character's own inventory - costs their turn exactly
        /// like <see cref="CharacterAttack"/>, consumes one unit of the stack, and always targets
        /// the user (potions; no ally-targeted items exist yet). Generates no aggro: it neither
        /// damages nor heals anyone but the user.
        /// </summary>
        /// <returns>false (no turn spent) if it isn't <paramref name="playerName"/>'s turn or
        /// <paramref name="item"/> isn't in their inventory.</returns>
        public bool CharacterUseItem(string playerName, ConsumableItem item)
        {
            if (IsFinished || !IsThisCharactersTurn(playerName)) return false;

            var user = Characters[_currentCharacterIndex];
            if (!user.Inventory.Items.Contains(item)) return false;

            bool userStunned = EffectProcessor.IsStunned(user);
            foreach (var entry in EffectProcessor.Tick(user))
                Log.Add(entry);

            if (!user.IsAlive)
            {
                AdvanceAfterCharacterAction();
                return false;
            }

            if (userStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", user.Name));
                AdvanceAfterCharacterAction();
                return true;
            }

            item.Use(user);
            user.Inventory.ConsumeOne(item);
            Log.Add(new CombatLogEntry("pg.fight.log.usedItem", user.Name, item.Name));

            AdvanceAfterCharacterAction();
            return true;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private void ExecuteSkillOnCharacter(Skill skill, Character caster, Character target)
        {
            if (LastingBuff.From(skill, caster.Name) is { } lasting)
            {
                target.ApplyLastingBuff(lasting);
                Log.Add(new CombatLogEntry("pg.fight.log.lastingBuff", caster.Name, target.Name, skill.Name, skill.LastingBuff!.DurationMinutes));
            }
            if (!skill.IsHealing) return;
            int baseStat = ResolveBaseStat(skill, caster);
            int heal     = CombatEntity.ScaleHeal((int)(baseStat * skill.ScalingFactor));
            int healed   = Math.Min(heal, target.MaxHealth - target.CurrentHealth);
            target.Heal(healed);
            Log.Add(new CombatLogEntry("pg.fight.log.heal", caster.Name, healed));
        }

        private int ExecuteSkillOnMonster(Skill skill, Character caster, ICombatant target)
        {
            if (skill.IsHealing) return 0;
            int dmg = CombatSystem.SkillDamage(caster, skill, target);
            target.TakeDamage(dmg);
            Log.Add(new CombatLogEntry("pg.fight.log.skillHit", caster.Name, skill.Name, dmg));
            return dmg;
        }

        /// <summary>Who gets every drop of this fight (a party's loot master in a dungeon). Null, or
        /// not part of the fight: the first living player, as before.</summary>
        public Character? LootRecipient { get; set; }

        private void HandleMonsterDeath(Monster monster)
        {
            if (monster.IsAlive) return;

            bool monsterHasAllies = Monsters.Any(m => m.IsAlive && m != monster);
            if (EffectProcessor.TryResurrect(monster, monsterHasAllies))
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.resurrected", monster.Name));
                return;
            }

            Log.Add(new CombatLogEntry("pg.fight.log.win", monster.Name));
            MonsterKilled?.Invoke(this, new MonsterKilledEventArgs(monster.Id, monster));

            // Quest kill-credit goes to every party member, alive or not (matches prior behavior);
            // XP/class-XP is alive-only, handled separately below.
            foreach (var p in Characters)
                GameEvents.FireMonsterKilled(p, monster);

            foreach (var p in Characters.Where(p => p.IsAlive))
            {
                long xpGained = ScaleXp(monster.Exp, p.Level, monster.Level);
                p.GainXp(xpGained);
                ClassManager.GrantClassXp(p, xpGained);
                SkillFactory.UpdateSkills(p);
                XpGrantedByCharacter[p] = XpGrantedByCharacter.GetValueOrDefault(p) + xpGained;
            }

            // The party's loot master when one is set for this fight (dungeons; alive or not - they
            // hand the loot out themselves through trades), otherwise the first living player.
            var recipient = LootRecipient is not null && Characters.Contains(LootRecipient)
                ? LootRecipient
                : Characters.FirstOrDefault(p => p.IsAlive);
            if (recipient != null)
            {
                var drops = LootGenerator.GetLootFor(monster);
                if (!LootGrantedByCharacter.TryGetValue(recipient, out var lootList))
                    LootGrantedByCharacter[recipient] = lootList = new();
                foreach (var drop in drops)
                {
                    if (drop.StackSize == 0) drop.StackSize = 1;
                    recipient.Inventory.AddItem(drop, recipient);
                    // One list entry per unit (matches the client's ItemFactory.CreateItem-per-
                    // entry mirror in ApplyServerXpGain) - a plain single Add per drop here used
                    // to discard drop.StackSize, so a kill dropping e.g. 2x of an item only ever
                    // granted 1 client-side until the next relog, even though the server's own
                    // Inventory.AddItem call two lines up already had the correct count.
                    for (int i = 0; i < drop.StackSize; i++)
                        lootList.Add(drop.Id);
                }
            }

            if (Monsters.All(m => !m.IsAlive))
                FinishCharactersWon();
        }

        private void AdvanceAfterCharacterAction()
        {
            // The actor's turn is over: their own skill cooldowns go down by one (cooldowns count
            // the character's turns), and their next turn moves back by their delay.
            // _currentCharacterIndex still points at the actor here.
            var actor = Characters[_currentCharacterIndex];
            if (_skillCooldowns.TryGetValue(actor, out var actorCooldowns) && actorCooldowns.Count > 0)
            {
                foreach (var key in actorCooldowns.Keys.ToList())
                {
                    if (--actorCooldowns[key] <= 0)
                        actorCooldowns.Remove(key);
                }
            }
            _nextTurnAt[actor] = _now + TurnDelay(actor);
            AdvanceTimeline();
        }

        /// <summary>
        /// Plays the timeline forward to the next living player's turn: monsters whose turn comes
        /// first act on their own (each at its own time). Ties go to players, then roster order.
        /// </summary>
        private void AdvanceTimeline()
        {
            for (int guard = 0; guard < 10_000 && !IsFinished; guard++)
            {
                if (!Characters.Any(c => c.IsAlive)) { FinishCharactersLost(); return; }

                CombatEntity? next = null;
                double nextAt = double.MaxValue;
                foreach (var c in Characters.Where(c => c.IsAlive))
                {
                    double at = _nextTurnAt.TryGetValue(c, out var v) ? v : (_nextTurnAt[c] = _now + TurnDelay(c));
                    if (at < nextAt) { next = c; nextAt = at; }
                }
                foreach (var m in Monsters.Where(m => m.IsAlive))
                {
                    double at = _nextTurnAt.TryGetValue(m, out var v) ? v : (_nextTurnAt[m] = _now + TurnDelay(m));
                    if (at < nextAt) { next = m; nextAt = at; }   // strict: a tie stays with the player
                }
                if (next is null) return;   // no living monster: the fight is already finished

                _now = Math.Max(_now, nextAt);
                if (next is Character character)
                {
                    _currentCharacterIndex = _characters.IndexOf(character);
                    return;
                }

                var monster = (Monster)next;
                MonsterTurn(monster);
                _nextTurnAt[monster] = _now + TurnDelay(monster);
            }
        }

        /// <summary>One monster's turn: its effects tick (poison may kill it), then it attacks the
        /// character with the most aggro unless it's stunned.</summary>
        private void MonsterTurn(Monster monster)
        {
            var living = Characters.Where(p => p.IsAlive).ToList();
            if (living.Count == 0) { FinishCharactersLost(); return; }

            // Credit 0.5 aggro per Poison DoT tick to the character who applied each effect.
            foreach (var effect in monster.ActiveEffects.Where(e => e.Type == EffectType.Poison && e.TurnsRemaining > 0))
            {
                var source = living.FirstOrDefault(c => c.Name == effect.SourceCharacterName);
                if (source != null)
                    source.AggroLevel += 0.5f;
            }

            bool monsterStunned = EffectProcessor.IsStunned(monster);
            foreach (var entry in EffectProcessor.Tick(monster))
                Log.Add(entry);

            // Killed by its own poison: a death like any other - resurrection check, XP, loot, quest
            // credit, and the end of the fight once it was the last one.
            if (!monster.IsAlive)
            {
                HandleMonsterDeath(monster);
                return;
            }

            if (monsterStunned)
            {
                Log.Add(new CombatLogEntry("pg.fight.effect.stunned", monster.Name));
                return;
            }

            // Target the character with the highest aggro; on ties, first in initiative order.
            var target = living.OrderByDescending(c => c.AggroLevel).First();

            int dmg = CombatSystem.CalculateDamage(monster, target);
            if (dmg <= 0)
                Log.Add(new CombatLogEntry("pg.fight.log.enemyMiss", monster.Name));
            else
            {
                target.ApplyDamage(dmg);
                Log.Add(new CombatLogEntry("pg.fight.log.enemyHit", monster.Name, dmg));

                if (!target.IsAlive)
                {
                    bool hasCharAllies = Characters.Any(c => c.IsAlive && c != target);
                    if (EffectProcessor.TryResurrect(target, hasCharAllies))
                        Log.Add(new CombatLogEntry("pg.fight.effect.resurrected", target.Name));
                }
            }

            if (!Characters.Any(p => p.IsAlive)) FinishCharactersLost();
        }

        private void FinishCharactersWon()
        {
            IsFinished = true;
            CharactersWon = true;
            foreach (var p in Characters)
                SkillFactory.UpdateSkills(p);
        }

        private void FinishCharactersLost()
        {
            IsFinished = true;
            CharactersWon = false;
            Log.Add(new CombatLogEntry("pg.fight.log.lose"));
        }

        private static long ScaleXp(long baseXp, int playerLevel, int monsterLevel)
        {
            if (playerLevel <= monsterLevel) return baseXp;
            double ratio = (double)monsterLevel / playerLevel;
            return Math.Max(1L, (long)(baseXp * ratio * ratio));
        }

        private bool IsThisCharactersTurn(string playerName) =>
            _currentCharacterIndex < Characters.Count &&
            string.Equals(Characters[_currentCharacterIndex].Name, playerName, StringComparison.OrdinalIgnoreCase);

        private Monster? GetMonster(int index) =>
            index >= 0 && index < Monsters.Count ? Monsters[index] : null;

        private Character? GetCharacter(int index) =>
            index >= 0 && index < Characters.Count ? Characters[index] : null;

        private static int ResolveBaseStat(Skill skill, Character character) => CombatSystem.SkillBaseStat(character, skill);

        private static int ResolveEffectStat(Skill skill, Character character) => CombatSystem.SkillEffectStat(character, skill);
    }
}
