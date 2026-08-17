using Microsoft.AspNetCore.Mvc;

namespace TriviaServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlayersController : ControllerBase
    {
        private readonly PlayerProfileStore _profiles;

        public PlayersController(PlayerProfileStore profiles)
        {
            _profiles = profiles;
        }

        // GET /api/players/{persistentPlayerId}
        // Returns 404 if this id has never played a match - the client
        // treats that as "new player, show the default starting Elo"
        // rather than an error.
        [HttpGet("{persistentPlayerId}")]
        public async Task<ActionResult<PlayerProfileResult>> Get(string persistentPlayerId)
        {
            var profile = await _profiles.TryGetAsync(persistentPlayerId);
            if (profile == null) return NotFound();

            return Ok(new PlayerProfileResult
            {
                PersistentPlayerId = persistentPlayerId,
                Name = profile.Value.Name,
                Elo = profile.Value.Elo
            });
        }
    }
}
