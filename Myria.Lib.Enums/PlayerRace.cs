namespace Myria.Lib.Core.Systems.Enums
{
    /// <summary>
    /// Built-in character race identifiers. String-based like <see cref="CharacterClass"/>
    /// so that mod-added races work without code changes.
    /// </summary>
    public static class CharacterRace
    {
        public const string Rotuka = "Rotuka";
        public const string Iymva  = "Iymva";
        public const string Myralu = "Myralu";
        public const string Elf    = "Elf";
        public const string Faerie = "Faerie";
        public const string Aoyu   = "Aoyu";
        public const string Amato  = "Amato";

        /// <summary>Retired in v0.3 (not part of the story's peoples); characters of these races are
        /// moved to a lore race on load - see RaceMigration.</summary>
        public const string Zalu   = "Zalu";
        public const string Gavon  = "Gavon";
        public const string Gamato = "Gamato";

        /// <summary>All built-in, playable race IDs.</summary>
        public static readonly IReadOnlyList<string> AllBuiltIn = new[]
        {
            Rotuka, Iymva, Myralu, Elf, Faerie, Aoyu, Amato
        };

        /// <summary>Maps the old enum integer values to string IDs (for save-file migration).</summary>
        public static readonly IReadOnlyDictionary<int, string> FromLegacyInt =
            new Dictionary<int, string>
            {
                [0] = Rotuka,
                [1] = Iymva,
                [2] = Myralu,
                [3] = Zalu,
                [4] = Gavon,
                [5] = Gamato,
                [6] = Amato,
            };
    }
}
