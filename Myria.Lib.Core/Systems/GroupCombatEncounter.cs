using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Effects;
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

        // ── DEX-driven bonus turns ──────────────────────────────────────────────
        // Turn order itself is still the initiative roll from the constructor (unchanged) - this
        // only lets a character who's fast *relative to this fight's other participants* earn
        // extra actions back-to-back on top of their normal turn, instead of a flat one-action-
        // per-character-per-round cadence regardless of DEX. Each character accumulates a gauge
        // by their own TotalDEX every time their normal turn comes up; once it crosses
        // _speedGaugeThreshold (set once, from this encounter's average DEX, so it self-scales to
        // whatever level/gear range the party is actually at) they immediately get another action
        // before play moves on, consuming the gauge. A capped number of bonus actions per arrival
        // keeps one extreme DEX outlier from monopolizing the whole round.
        private readonly Dictionary<Character, double> _speedGauge = new();
        private readonly double _speedGaugeThreshold;
        private const int MaxBonusActionsPerTurn = 3;
        private int _bonusActionsRemaining;

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

            // 2.5x the party's own average DEX: an exactly-average character earns one bonus
            // action roughly every 2-3 of their normal turns, a character at 2x average roughly
            // every turn, and one well below average essentially never - self-scales to whatever
            // level/gear range this specific fight's participants are at rather than a flat
            // magic number that would be trivial at high level and unreachable at low level.
            _speedGaugeThreshold = Math.Max(1.0, Characters.Average(c => c.TotalDEX) * 2.5);

            Log.Add(new CombatLogEntry("pg.fight.log.start",
                string.Join(", ", Monsters.Select(m => m.Name))));
            Log.Add(new CombatLogEntry("pg.fight.log.order",
                string.Join(", ", Characters.Select(p => p.Name))));
            SkipDeadCharacters();
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
        /// the (now longer) roster. Uses the encounter's existing <see cref="_speedGaugeThreshold"/>
        /// rather than recalculating it — a late joiner shouldn't retroactively change everyone
        /// else's bonus-turn pacing.
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
        /// <see cref="SkipDeadCharacters"/>/<see cref="MonstersTurn"/>'s "nobody left standing"
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

                if (_characters.Count == 0) { FinishCharactersLost(); return; }

                if (idx < _currentCharacterIndex)
                {
                    // An earlier slot was removed - shift down so the index still points at the
                    // same character whose turn was already correctly in progress.
                    _currentCharacterIndex--;
                    return;
                }

                if (!wasCurrentTurn) return; // idx > _currentCharacterIndex: nothing to adjust

                // The character who just left was up (or next up) - any bonus turns queued for
                // them are now moot, and removal already shifted everyone after them down by one,
                // so _currentCharacterIndex (left unchanged) now already points at whoever's next.
                _bonusActionsRemaining = 0;
                while (_currentCharacterIndex < _characters.Count && !Characters[_currentCharacterIndex].IsAlive)
                    _currentCharacterIndex++;

                if (_currentCharacterIndex >= _characters.Count)
                {
                    MonstersTurn();
                    if (!IsFinished) SkipDeadCharacters();
                }
                else
                {
                    GrantGaugeForCurrentCharacter();
                }
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

                    if (effect.Type == EffectType.LifeSteal)
                    {
                        int healAmount = Math.Max(1, (int)(totalDamageForLifesteal * effect.Magnitude));
                        caster.Heal(healAmount);
                        Log.Add(new CombatLogEntry("pg.fight.effect.lifesteal", caster.Name, healAmount));
                    }
                    else if (effect.Type == EffectType.ManaSiphon)
                    {
                        int manaAmount = Math.Max(1, (int)(totalDamageForLifesteal * effect.Magnitude));
                        caster.RestoreMana(manaAmount);
                        Log.Add(new CombatLogEntry("pg.fight.effect.manasiphon", caster.Name, manaAmount));
                    }
                    else
                    {
                        EffectProcessor.Apply(host, effect);
                        Log.Add(new CombatLogEntry("pg.fight.effect.applied", effect.Name, host.Name));
                    }
                }
            }

            skill.Effect?.Invoke(caster, GetMonster(targetIndex) ?? (ICombatant)caster);

            if (!IsFinished) AdvanceAfterCharacterAction();
            return true;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private void ExecuteSkillOnCharacter(Skill skill, Character caster, Character target)
        {
            if (!skill.IsHealing) return;
            int baseStat = ResolveBaseStat(skill, caster);
            int heal     = (int)(baseStat * skill.ScalingFactor);
            int healed   = Math.Min(heal, target.MaxHealth - target.CurrentHealth);
            target.Heal(healed);
            Log.Add(new CombatLogEntry("pg.fight.log.heal", caster.Name, healed));
        }

        private int ExecuteSkillOnMonster(Skill skill, Character caster, ICombatant target)
        {
            if (skill.IsHealing) return 0;
            int baseStat = ResolveBaseStat(skill, caster);
            float raw = baseStat * skill.ScalingFactor;
            float def = skill.Type == SkillType.Physical
                ? target.TotalPhysicalDefense
                : target.TotalMagicDefense;
            int dmg = Math.Max(1, (int)(raw * (raw / (raw + def))));
            target.TakeDamage(dmg);
            Log.Add(new CombatLogEntry("pg.fight.log.skillHit", caster.Name, skill.Name, dmg));
            return dmg;
        }

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
            MonsterKilled?.Invoke(this, new MonsterKilledEventArgs(monster.Id));

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

            var recipient = Characters.FirstOrDefault(p => p.IsAlive);
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

            // Mirrors CombatEncounter.FinishCharacterWon's dungeon room-clearing - previously only
            // the solo path did this, so dungeon monsters killed via group combat never left the
            // room and dungeons could never be marked cleared.
            var room = Characters.FirstOrDefault()?.CurrentRoom;
            if (room != null && room.IsDungeonRoom)
            {
                room.CurrentMonsters.Remove(monster);
                if (room.CurrentMonsters.Count == 0)
                    room.IsCleared = true;
            }

            if (Monsters.All(m => !m.IsAlive))
                FinishCharactersWon();
        }

        private void AdvanceAfterCharacterAction()
        {
            // Decrement whoever just acted's own skill cooldowns - _currentCharacterIndex still
            // points at them here, before any of the advancement logic below runs.
            var actor = Characters[_currentCharacterIndex];
            if (_skillCooldowns.TryGetValue(actor, out var actorCooldowns) && actorCooldowns.Count > 0)
            {
                foreach (var key in actorCooldowns.Keys.ToList())
                {
                    if (--actorCooldowns[key] <= 0)
                        actorCooldowns.Remove(key);
                }
            }

            // A DEX-earned bonus action keeps the turn on the same character instead of moving
            // on - CurrentTurnCharacterName is unchanged, so no protocol/client change is needed
            // for this to work; the client just gets prompted for the same character's action again.
            if (_bonusActionsRemaining > 0)
            {
                _bonusActionsRemaining--;
                return;
            }

            int next = _currentCharacterIndex + 1;
            while (next < Characters.Count && !Characters[next].IsAlive) next++;

            if (next >= Characters.Count)
            {
                // All living players have acted — monsters' turn
                MonstersTurn();
                if (!IsFinished)
                    SkipDeadCharacters(); // restart from first living player
            }
            else
            {
                _currentCharacterIndex = next;
                GrantGaugeForCurrentCharacter();
            }
        }

        private void SkipDeadCharacters()
        {
            int idx = 0;
            while (idx < Characters.Count && !Characters[idx].IsAlive) idx++;
            _currentCharacterIndex = idx;
            if (_currentCharacterIndex >= Characters.Count) { FinishCharactersLost(); return; }
            GrantGaugeForCurrentCharacter();
        }

        /// <summary>Adds this round's DEX to the current character's speed gauge and queues
        /// however many bonus actions (capped) that crosses the threshold for.</summary>
        private void GrantGaugeForCurrentCharacter()
        {
            var character = Characters[_currentCharacterIndex];
            double gauge = _speedGauge.GetValueOrDefault(character) + character.TotalDEX;

            int bonus = 0;
            while (gauge >= _speedGaugeThreshold && bonus < MaxBonusActionsPerTurn)
            {
                gauge -= _speedGaugeThreshold;
                bonus++;
            }

            _speedGauge[character] = gauge;
            _bonusActionsRemaining = bonus;

            if (bonus > 0)
                Log.Add(new CombatLogEntry("pg.fight.log.bonusTurn", character.Name, bonus));
        }

        private void MonstersTurn()
        {
            var living = Characters.Where(p => p.IsAlive).ToList();
            if (living.Count == 0) { FinishCharactersLost(); return; }

            foreach (var monster in Monsters.Where(m => m.IsAlive).ToList())
            {
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

                if (!monster.IsAlive) continue;

                if (monsterStunned)
                {
                    Log.Add(new CombatLogEntry("pg.fight.effect.stunned", monster.Name));
                    continue;
                }

                // Target the character with the highest aggro; on ties, first in initiative order.
                var currentLiving = Characters.Where(c => c.IsAlive).ToList();
                if (currentLiving.Count == 0) break;
                var target = currentLiving.OrderByDescending(c => c.AggroLevel).First();

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

        private static int ResolveBaseStat(Skill skill, Character character) =>
            skill.StatToScaleFrom.ToUpper() switch
            {
                "ATK"  => character.TotalPhysicalAttack,
                "MATK" => character.TotalMagicAttack,
                "SPR"  => character.TotalSPR,
                "INT"  => character.TotalINT,
                "DEX"  => character.TotalDEX,
                "AIM"  => character.TotalAim * 2,
                "EVA"  => character.TotalEvasion * 2,
                "END"  => character.TotalEND,
                "STR"  => character.TotalSTR,
                _      => character.TotalPhysicalAttack
            };

        // Uses the caster's PRIMARY stat for effect magnitude scaling.
        // ATK/MATK map to their underlying stat (STR/INT) so all skills
        // start at a similar baseline (~8-10 at level 1).
        private static int ResolveEffectStat(Skill skill, Character character) =>
            skill.StatToScaleFrom.ToUpper() switch
            {
                "ATK"                   => character.TotalSTR,
                "MATK"                  => character.TotalINT,
                "SPR"                   => character.TotalSPR,
                "INT"                   => character.TotalINT,
                "DEX" or "AIM" or "EVA" => character.TotalDEX,
                "END"                   => character.TotalEND,
                "STR"                   => character.TotalSTR,
                _                       => character.TotalSTR
            };
    }
}
