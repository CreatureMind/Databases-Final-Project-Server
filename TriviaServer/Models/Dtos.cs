namespace TriviaServer
{
    public class JoinRequest
    {
        // Null/empty on a player's first-ever launch; sent back from
        // PlayerPrefs on every launch after that.
        public string? PersistentPlayerId { get; set; }
        public string Name { get; set; } = "";
    }

    // Returned by both POST /match/join and GET /match/status/{playerId}.
    public class JoinResult
    {
        public bool Matched { get; set; }
        public int PlayerId { get; set; }       // this match session's id - use for status/submit-score/result
        public int? MatchId { get; set; }
        public string PersistentPlayerId { get; set; } = "";
        public int MyElo { get; set; }
        public string? OpponentName { get; set; }   // set once Matched == true
        public int? OpponentElo { get; set; }
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
        public int? EloChange { get; set; }  // set once the match is completed
        public int? NewElo { get; set; }     // set once the match is completed
    }

    // Returned by GET /players/{persistentPlayerId} - for showing a
    // player's own Elo in a menu, outside of matchmaking.
    public class PlayerProfileResult
    {
        public string PersistentPlayerId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Elo { get; set; }
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
