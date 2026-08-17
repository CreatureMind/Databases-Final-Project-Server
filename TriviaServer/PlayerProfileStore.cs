using StackExchange.Redis;

namespace TriviaServer
{
    // The NoSQL half of the stack: one Redis hash per player, keyed by the
    // persistent id Unity caches in PlayerPrefs. Postgres never stores Elo
    // directly - it only ever holds short-lived snapshots for a single match.
    public class PlayerProfileStore
    {
        private const int StartingElo = 1000;

        private readonly IConnectionMultiplexer _redis;

        public PlayerProfileStore(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        private IDatabase Db => _redis.GetDatabase();
        private static string Key(string playerId) => $"player:{playerId}";

        // persistentPlayerId is null/empty on a player's very first launch.
        // If it's provided but not found in Redis (e.g. Redis was flushed),
        // a fresh profile is created reusing that same id, so the client's
        // PlayerPrefs value stays valid either way.
        public async Task<(string PersistentPlayerId, string Name, int Elo)> GetOrCreateAsync(
            string? persistentPlayerId, string name)
        {
            var id = string.IsNullOrWhiteSpace(persistentPlayerId)
                ? Guid.NewGuid().ToString("N")
                : persistentPlayerId;

            var key = Key(id);
            var existing = await Db.HashGetAllAsync(key);

            if (existing.Length > 0)
            {
                var fields = existing.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
                var elo = int.Parse(fields.GetValueOrDefault("elo", StartingElo.ToString()));

                if (fields.GetValueOrDefault("name") != name)
                    await Db.HashSetAsync(key, "name", name);

                return (id, name, elo);
            }

            await Db.HashSetAsync(key, new HashEntry[]
            {
                new("name", name),
                new("elo", StartingElo)
            });

            return (id, name, StartingElo);
        }

        // Read-only lookup for showing a player's profile (e.g. in a main
        // menu) without matchmaking side effects. Returns null if this id
        // has never played - the caller decides what a "new player" shows.
        public async Task<(string Name, int Elo)?> TryGetAsync(string persistentPlayerId)
        {
            var existing = await Db.HashGetAllAsync(Key(persistentPlayerId));
            if (existing.Length == 0) return null;

            var fields = existing.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            var elo = int.Parse(fields.GetValueOrDefault("elo", StartingElo.ToString()));
            var name = fields.GetValueOrDefault("name", "");
            return (name, elo);
        }

        public Task UpdateEloAsync(string persistentPlayerId, int newElo) =>
            Db.HashSetAsync(Key(persistentPlayerId), "elo", newElo);
    }
}
