using Microsoft.AspNetCore.Mvc;

namespace TriviaServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MatchController : ControllerBase
    {
        private readonly DatabaseManager _db;

        public MatchController(DatabaseManager db)
        {
            _db = db;
        }

        // POST /api/match/join
        // Call this when the player wants to start a game. If someone else
        // is already waiting, the response comes back already matched. If
        // not, the client should poll /api/match/status/{playerId}.
        [HttpPost("join")]
        public async Task<ActionResult<JoinResult>> Join([FromBody] JoinRequest request)
        {
            var name = string.IsNullOrWhiteSpace(request.Name) ? "Player" : request.Name;
            var result = await _db.JoinMatchAsync(request.PersistentPlayerId, name);
            return Ok(result);
        }

        // GET /api/match/status/5
        [HttpGet("status/{playerId:int}")]
        public async Task<ActionResult<JoinResult>> Status(int playerId)
        {
            try
            {
                var result = await _db.GetMatchStatusForWaitingPlayerAsync(playerId);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        // POST /api/match/submit-score
        // Call this once, when the local player finishes all their questions.
        [HttpPost("submit-score")]
        public async Task<IActionResult> SubmitScore([FromBody] SubmitScoreRequest request)
        {
            await _db.SubmitScoreAsync(request.PlayerId, request.CorrectCount, request.TotalTimeMs);
            return Ok();
        }

        // GET /api/match/result/5
        // Poll this after submit-score until Completed == true.
        [HttpGet("result/{playerId:int}")]
        public async Task<ActionResult<MatchResult>> Result(int playerId)
        {
            var result = await _db.GetMatchResultAsync(playerId);
            if (result == null) return NotFound();
            return Ok(result);
        }
    }
}
