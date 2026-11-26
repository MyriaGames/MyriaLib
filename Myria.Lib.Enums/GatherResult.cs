namespace Myria.Lib.Core.Systems.Enums
{
    public enum GatherResult
    {
        Success,
        NoSpots,       // room has no gathering spots
        Depleted,      // all gathers used up for this game-day
        NoTool,        // player lacks the required tool
        InventoryFull,
        SkillTooLow,   // v0.3: every spot the player has the tool for needs a higher job skill level
    }
}
