namespace TriviaServer
{
    public class JoinRequest
    {
        public string Name { get; set; } = "";
    }

    // Returned by both POST /match/join and GET /match/status/{playerId}.
    public class JoinResult
    {
        public bool Matched { get; set; }
        public int PlayerId { get; set; }
        public int? MatchId { get; set; }
    }

    public class SubmitScoreRequest
    {
        public int PlayerId { get; set; }
        public int CorrectCount { get; set; }
        public int TotalTimeMs { get; set; }
    }

    public class PlayerResult
    {
        public int PlayerId { get; set; }
        public string Name { get; set; } = "";
        public int CorrectCount { get; set; }
        public int TotalTimeMs { get; set; }
    }

    // Returned by GET /match/result/{playerId}. Poll this until
    // Completed == true, then use WinnerId / IsTie to show the outcome.
    public class MatchResult
    {
        public int MatchId { get; set; }
        public bool Completed { get; set; }
        public int? WinnerId { get; set; }
        public bool IsTie { get; set; }
        public List<PlayerResult> Players { get; set; } = new();
    }
}
