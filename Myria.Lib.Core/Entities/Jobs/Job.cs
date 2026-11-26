namespace Myria.Lib.Core.Entities.Jobs
{
    public class Job
    {
        public string Id   { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Type { get; set; } = "";  // e.g. "Gathering", "Crafting"

        /// <summary>
        /// Opt-in: a character with this as their active job can craft its master NPC's recipes
        /// from the Jobs page, without standing at that NPC ("crafting on the go"). Deliberately
        /// per-job rather than inferred from the NPC offering "craft" - smiths, tailors etc. must
        /// keep requiring their NPC. The server enforces this (GameHub.CraftFromJob).
        /// </summary>
        public bool AllowJobPageCrafting { get; set; }
    }
}
