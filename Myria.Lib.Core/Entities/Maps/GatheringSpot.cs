namespace Myria.Lib.Core.Entities.Maps
{
    public class GatheringSpot
    {
        public string Id { get; set; }           // e.g., "iron_vein"
        public string Name { get; set; }         // e.g., "Iron Vein"
        public string Description { get; set; }
        public string Type { get; set; }
        public string GatheredItemId { get; set; } // e.g., "iron_ore"
        public string? RequiredToolId { get; set; } // e.g., "pickaxe" — null means no tool required
        /// <summary>v0.3: minimum gathering-job skill level (miner/woodcutter/herbalist) to work this
        /// spot - rarer resources need more practice. 0 = anyone.</summary>
        public int RequiredSkillLevel { get; set; }
    }

}
