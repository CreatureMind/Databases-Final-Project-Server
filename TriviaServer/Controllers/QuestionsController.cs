using Microsoft.AspNetCore.Mvc;
using TriviaServer.Models;

namespace TriviaServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class QuestionsController : ControllerBase
    {
        private readonly DatabaseManager _db;

        public QuestionsController(DatabaseManager db)
        {
            _db = db;
        }

        // GET /api/questions
        [HttpGet]
        public async Task<ActionResult<List<Question>>> Get()
        {
            var questions = await _db.GetQuestionsAsync();
            return Ok(questions);
        }
    }
}
