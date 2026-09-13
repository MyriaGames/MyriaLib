using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Utils;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myria.Lib.Core.Services.Builder
{
    public static class SkillFactory
    {
        private static List<Skill> _skills = new();
        private static Dictionary<string, Skill> _skillsById = new();
        // private static List<BaseSkill> _baseSkills = new();

        public static void LoadSkills(string path = "Data/common/skills.json")
        {
            if (!File.Exists(path))
            {
                GameLog.Error($"Skills file not found at '{path}' — no skills loaded.");
                _skills = new();
                _skillsById = new();
                return;
            }

            var skillJson = File.ReadAllText(path);
            var jsonOptions = new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            };
            var skillData = JsonSerializer.Deserialize<List<SkillData>>(skillJson, jsonOptions);
            if (skillData == null)
            {
                GameLog.Error($"Failed to deserialize skills from '{path}'.");
                _skills = new();
                _skillsById = new();
                return;
            }

            LoadSkills(skillData);
        }

        /// <summary>Loads skills from already-parsed data (e.g. read from a database).</summary>
        public static void LoadSkills(List<SkillData> skillData)
        {
            _skills = skillData.Select(d => new Skill
            {
                Id              = d.Id,
                Name            = d.Name,
                Description     = d.Description,
                Class           = d.Class,
                ManaCost        = d.ManaCost,
                Type            = Enum.Parse<SkillType>(d.Type),
                Target          = d.Target,
                ScalingFactor   = d.ScalingFactor,
                StatToScaleFrom = d.StatToScaleFrom,
                MinLevel        = d.MinLevel,
                IsHealing       = d.IsHealing,
                AggroModifier   = d.AggroModifier,
                Cooldown        = d.Cooldown,
                Effects         = d.Effects,
            }).ToList();

            _skillsById = _skills.ToDictionary(s => s.Id);
        }

        /// <summary>Returns a skill by ID, or <c>null</c> if not found.</summary>
        public static Skill? GetSkill(string id) =>
            _skillsById.TryGetValue(id, out var skill) ? skill : null;

        public static List<Skill> GetSkillsFor(Character character)
        {
            return _skills.Where(s => s.Class.Equals(character.Class, StringComparison.OrdinalIgnoreCase) && s.MinLevel <= character.Level).ToList();
        }
        //public static List<BaseSkill> GetBaseSkillsFor(Character character)
        //{
        //    return _baseSkills
        //        .Where(bs => bs.Class == character.Class && bs.RequiredLevel <= character.Level)
        //        .ToList();
        //}
        public static void UpdateSkills(Character character)
        {
            // Remove any skills that don't belong to the character's current class.
            // This cleans up wrong skills set before the class was known (e.g. from saves
            // written with a stale default class) without touching combined/composite skills.
            character.Skills.RemoveAll(s => !s.Class.Equals(character.Class, StringComparison.OrdinalIgnoreCase));

            var unlocked = GetSkillsFor(character);
            foreach (var skill in unlocked)
            {
                character.LearnSkill(skill);
            }

            if (character.Class.Equals(CharacterClass.RunicMage, StringComparison.OrdinalIgnoreCase))
                BaseRuneService.GrantBaseRunes(character);
        }

    }

}
