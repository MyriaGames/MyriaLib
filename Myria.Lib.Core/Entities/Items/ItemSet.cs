namespace Myria.Lib.Core.Entities.Items
{
    /// <summary>
    /// A gear set (v0.3, e.g. a dungeon's loot set): wearing several of its pieces gives skill
    /// bonuses - from two pieces on, and each further tier adds more (tiers stack). Keeps an older
    /// set useful at higher levels for its effect, not just its stats.
    /// </summary>
    public class ItemSet
    {
        public string Id { get; set; } = "";
        /// <summary>Localization key of the set's name.</summary>
        public string Name { get; set; } = "";
        public List<string> Items { get; set; } = new();
        public List<ItemSetTier> Tiers { get; set; } = new();
    }

    public class ItemSetTier
    {
        /// <summary>Equipped pieces of the set needed for this tier.</summary>
        public int Pieces { get; set; }
        public List<SkillSetBonus> Bonuses { get; set; } = new();
    }

    /// <summary>A bonus to one skill. Power and ManaCost are fractions of the skill's own values
    /// (0.10 = +10% power, -0.20 = 20% cheaper); Cooldown is in turns (-1 = one turn shorter).</summary>
    public class SkillSetBonus
    {
        public string SkillId { get; set; } = "";
        public float Power { get; set; }
        public float ManaCost { get; set; }
        public int Cooldown { get; set; }
    }
}
