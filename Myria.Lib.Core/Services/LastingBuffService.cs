using Myria.Lib.Core.Entities.Characters;
using System.Text.Json;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Models.Dto;

namespace Myria.Lib.Core.Services
{
    /// <summary>
    /// Casting a lasting-buff skill (v0.3; the Druid's Verdant Vigor) outside a fight: the same buff a
    /// cast in a fight gives, paid with the same mana, and it counts as a use of the skill. The realm
    /// decides who may be targeted (yourself or a party member in the same room) before calling this.
    /// </summary>
    public static class LastingBuffService
    {
        /// <summary>
        /// Casts <paramref name="skill"/> (the caster's effective, upgraded skill) on <paramref name="target"/>.
        /// On failure nothing changes and <paramref name="error"/> is a locale key.
        /// </summary>
        public static LastingBuff? TryCast(Character caster, Skill skill, Character target, out string? error)
        {
            error = null;
            if (skill.LastingBuff is null)            { error = "skill.cast.notOutOfCombat"; return null; }
            if (!caster.IsAlive || !target.IsAlive)   { error = "skill.cast.dead";           return null; }
            if (caster.CurrentMana < skill.ManaCost)  { error = "skill.cast.noMana";         return null; }

            caster.SpendMana(skill.ManaCost, "skill");
            var buff = target.ApplyLastingBuff(LastingBuff.From(skill, caster.Name)!);
            SkillLevelingService.GrantUsage(caster, skill.Id);
            return buff;
        }

        /// <summary>The still-active buffs as wire snapshots (CharacterUpdateDto, character load).</summary>
        public static List<LastingBuffSnapshot> Snapshots(IEnumerable<LastingBuff> buffs) =>
            buffs.Where(b => b.IsActive)
                 .Select(b => new LastingBuffSnapshot(b.SkillId, b.Name, b.CasterName, b.HpPercent, b.MpPercent, b.ExpiresUtc))
                 .ToList();

        /// <summary>Snapshots back to buffs (a client applying the server's list).</summary>
        public static List<LastingBuff> FromSnapshots(IEnumerable<LastingBuffSnapshot>? snapshots) =>
            (snapshots ?? Enumerable.Empty<LastingBuffSnapshot>()).Select(b => new LastingBuff
            {
                SkillId = b.SkillId, Name = b.Name, CasterName = b.CasterName,
                HpPercent = b.HpPercent, MpPercent = b.MpPercent, ExpiresUtc = b.ExpiresUtc,
            }).ToList();

        /// <summary>Database column form (a JSON array of the active buffs).</summary>
        public static string ToJson(IEnumerable<LastingBuff> buffs) =>
            JsonSerializer.Serialize(Snapshots(buffs));

        /// <summary>Reads <see cref="ToJson"/>; anything unreadable counts as no buffs. Expired ones are dropped.</summary>
        public static List<LastingBuff> FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return FromSnapshots(JsonSerializer.Deserialize<List<LastingBuffSnapshot>>(json)).Where(b => b.IsActive).ToList(); }
            catch (JsonException) { return new(); }
        }
    }
}
