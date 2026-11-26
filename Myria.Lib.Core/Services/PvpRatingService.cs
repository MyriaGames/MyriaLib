using Myria.Lib.Core.Entities.Characters;

namespace Myria.Lib.Core.Services
{
    /// <summary>One participant's PvP outcome, as applied to their character.</summary>
    public readonly record struct PvpResult(string Name, bool Won, int RatingDelta, int NewRating, long CurrencyDelta);

    /// <summary>
    /// PvP stakes: an Elo-style team rating plus a PvP-only currency - and nothing else. A loss
    /// never touches items, equipment, money, XP or stats (v0.3 PvP design). All numbers here are
    /// placeholders pending balance.
    /// </summary>
    public static class PvpRatingService
    {
        public const int StartingRating = 1000;
        /// <summary>Max rating swing per fight (Elo K-factor).</summary>
        public const int KFactor = 32;
        /// <summary>Every win moves at least this much, so a heavy favourite still gains/loses something.</summary>
        public const int MinRatingChange = 1;
        public const long WinCurrency = 10;
        /// <summary>Losers neither lose nor gain currency for now (open balance question in v0.3.md).</summary>
        public const long LossCurrency = 0;

        /// <summary>Elo expected score of a team rated <paramref name="rating"/> against one rated
        /// <paramref name="opponentRating"/> (0..1).</summary>
        public static double ExpectedScore(double rating, double opponentRating) =>
            1.0 / (1.0 + Math.Pow(10, (opponentRating - rating) / 400.0));

        /// <summary>
        /// The rating change for a win of a team averaging <paramref name="winnerAvg"/> over one
        /// averaging <paramref name="loserAvg"/>. Each winner gains it and each loser loses it -
        /// team ratings are averages, so a party fight moves everyone by the same amount.
        /// </summary>
        public static int RatingChange(double winnerAvg, double loserAvg) =>
            Math.Max(MinRatingChange, (int)Math.Round(KFactor * (1 - ExpectedScore(winnerAvg, loserAvg))));

        public static double AverageRating(IEnumerable<Character> team)
        {
            var list = team.ToList();
            return list.Count == 0 ? StartingRating : list.Average(c => c.PvpRating);
        }

        /// <summary>
        /// Applies a finished fight's result to every winner and loser still in it.
        /// <paramref name="losingSide"/> is the whole losing side the rating is computed against,
        /// including anyone who forfeited - if they all fled, <paramref name="losers"/> is empty but
        /// the winners still beat *them*, not some default rating.
        /// </summary>
        public static List<PvpResult> ApplyFightResult(IReadOnlyCollection<Character> winners, IReadOnlyCollection<Character> losers,
            IReadOnlyCollection<Character>? losingSide = null)
        {
            int change = RatingChange(AverageRating(winners), AverageRating(losingSide is { Count: > 0 } ? losingSide : losers));
            var results = new List<PvpResult>();
            foreach (var w in winners) results.Add(Apply(w, won: true, change));
            foreach (var l in losers)  results.Add(Apply(l, won: false, change));
            return results;
        }

        /// <summary>A forfeit (flee/disconnect) counts as a loss against the opposing side as it
        /// stands at that moment, applied immediately - the character may be gone by the time the
        /// fight finishes.</summary>
        public static PvpResult ApplyForfeit(Character loser, IReadOnlyCollection<Character> opponents)
        {
            int change = RatingChange(AverageRating(opponents), loser.PvpRating);
            return Apply(loser, won: false, change);
        }

        private static PvpResult Apply(Character c, bool won, int change)
        {
            int delta = won ? change : -Math.Min(change, c.PvpRating); // never below 0
            long currency = won ? WinCurrency : LossCurrency;
            c.PvpRating += delta;
            c.PvpCurrency += currency;
            if (won) c.PvpWins++; else c.PvpLosses++;
            return new PvpResult(c.Name, won, delta, c.PvpRating, currency);
        }
    }
}
