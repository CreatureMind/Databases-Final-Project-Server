namespace TriviaServer
{
    public enum EloOutcome { Win, Tie, Loss }

    // Standard Elo update. This is the authoritative copy - the client's
    // PlayerRatingCalculator.cs should be deleted once this is wired in,
    // since a client-computed rating change can't be trusted and would
    // just create a second, possibly-disagreeing source of truth.
    public static class EloCalculator
    {
        private const int KFactor = 32;

        public static int CalculateNewRating(int currentRating, int opponentRating, EloOutcome outcome)
        {
            double expectedScore = 1.0 / (1.0 + Math.Pow(10, (opponentRating - currentRating) / 400.0));

            // A tie is worth half a win, not a loss - the client version's
            // `isWin ? 1 : 0` had no way to express that.
            double actualScore = outcome switch
            {
                EloOutcome.Win => 1.0,
                EloOutcome.Tie => 0.5,
                _ => 0.0
            };

            return currentRating + (int)Math.Round(KFactor * (actualScore - expectedScore));
        }
    }
}
