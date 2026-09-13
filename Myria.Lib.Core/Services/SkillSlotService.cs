using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Skills;

namespace Myria.Lib.Core.Services
{
    public static class SkillSlotService
    {
        /// <summary>
        /// Re-populates <see cref="SkillSlot.ResolvedSkill"/> for all slots after a save load.
        /// </summary>
        public static void ResolveSlots(Character character)
        {
            foreach (var slot in character.SkillSlots)
                slot.ResolvedSkill = Resolve(character, slot);
        }

        /// <summary>
        /// If the character has no skill slots yet (new system, existing save), auto-fills up to
        /// <see cref="Character.SkillSlotCount"/> with the character's first learned regular skills.
        /// This is a no-op if <c>SkillSlots</c> is already populated.
        /// </summary>
        public static void MigrateIfEmpty(Character character)
        {
            if (character.SkillSlots.Count > 0) return;

            foreach (var skill in character.Skills.Take(character.SkillSlotCount))
            {
                character.SkillSlots.Add(new SkillSlot
                {
                    Source = SlottedSkillSource.Regular,
                    SkillId = skill.Id,
                    ResolvedSkill = skill
                });
            }
        }

        /// <summary>
        /// Adds a skill to the character's combat slots. Returns false if the cap is reached,
        /// the skill is already slotted, or the referenced skill cannot be found.
        /// </summary>
        public static bool TryAddSlot(Character character, SlottedSkillSource source, string skillId)
        {
            if (character.SkillSlots.Count >= character.SkillSlotCount) return false;
            if (character.SkillSlots.Any(s => s.Source == source && s.SkillId == skillId)) return false;

            var resolved = ResolveById(character, source, skillId);
            if (resolved == null) return false;

            character.SkillSlots.Add(new SkillSlot
            {
                Source = source,
                SkillId = skillId,
                ResolvedSkill = resolved
            });
            return true;
        }

        /// <summary>Removes the matching slot. Returns false if not found.</summary>
        public static bool RemoveSlot(Character character, SlottedSkillSource source, string skillId)
        {
            var slot = character.SkillSlots.FirstOrDefault(s => s.Source == source && s.SkillId == skillId);
            if (slot == null) return false;
            character.SkillSlots.Remove(slot);
            return true;
        }

        /// <summary>Moves the slot at <paramref name="fromIndex"/> to <paramref name="toIndex"/>.</summary>
        public static void ReorderSlots(Character character, int fromIndex, int toIndex)
        {
            var slots = character.SkillSlots;
            if (fromIndex < 0 || fromIndex >= slots.Count) return;
            if (toIndex < 0 || toIndex >= slots.Count) return;
            if (fromIndex == toIndex) return;

            var item = slots[fromIndex];
            slots.RemoveAt(fromIndex);
            slots.Insert(toIndex, item);
        }

        /// <summary>
        /// Returns the resolved skills for all active slots in order, with each character's own
        /// leveling/upgrades already applied (see SkillLevelingService.ResolveEffectiveSkill).
        /// Deliberately always resolves fresh rather than trusting SkillSlot.ResolvedSkill's cache -
        /// that cache is only safe for display purposes (e.g. a slot's skill name never changes),
        /// not for combat-facing stats, which can change mid-session as the character levels a
        /// skill up or buys an upgrade. Slots whose skill could not be resolved are skipped.
        /// </summary>
        public static IEnumerable<(Skill Skill, SlottedSkillSource Source)> GetCombatSkills(Character character)
        {
            foreach (var slot in character.SkillSlots)
            {
                var skill = Resolve(character, slot);
                if (skill != null)
                    yield return (skill, slot.Source);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static Skill? Resolve(Character character, SkillSlot slot) =>
            ResolveById(character, slot.Source, slot.SkillId);

        private static Skill? ResolveById(Character character, SlottedSkillSource source, string skillId)
        {
            var baseSkill = character.Skills.FirstOrDefault(s => s.Id == skillId);
            return baseSkill == null ? null : SkillLevelingService.ResolveEffectiveSkill(character, baseSkill);
        }
    }
}
