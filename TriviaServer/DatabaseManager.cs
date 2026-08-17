using Npgsql;
using TriviaServer.Models;

namespace TriviaServer
{
    public class DatabaseManager
    {
        private readonly string _connectionString;
        private readonly PlayerProfileStore _profiles;

        public DatabaseManager(IConfiguration configuration, PlayerProfileStore profiles)
        {
            _connectionString = configuration.GetConnectionString("SupabaseDb")
                ?? throw new InvalidOperationException(
                    "Connection string 'SupabaseDb' is missing. Set it via user-secrets or an environment variable, see SETUP.md.");
            _profiles = profiles;
        }

        private NpgsqlConnection CreateConnection() => new(_connectionString);

        // ---------------------------------------------------------------
        // Questions
        // ---------------------------------------------------------------

        public async Task<List<Question>> GetQuestionsAsync()
        {
            var result = new List<Question>();

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT id, question, answer_1, answer_2, answer_3, answer_4 FROM \"Questions\"",
                connection);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new Question
                {
                    Id = reader.GetInt32(reader.GetOrdinal("id")),
                    Text = reader.GetString(reader.GetOrdinal("question")),
                    Ans1 = reader.GetString(reader.GetOrdinal("answer_1")),
                    Ans2 = reader.GetString(reader.GetOrdinal("answer_2")),
                    Ans3 = reader.GetString(reader.GetOrdinal("answer_3")),
                    Ans4 = reader.GetString(reader.GetOrdinal("answer_4"))
                });
            }

            return result;
        }

        // ---------------------------------------------------------------
        // Matchmaking
        // ---------------------------------------------------------------

        // Resolves (or creates) the player's Redis profile first, then pairs
        // with whichever waiting player has the closest Elo.
        public async Task<JoinResult> JoinMatchAsync(string? persistentPlayerId, string name)
        {
            var (resolvedId, resolvedName, elo) = await _profiles.GetOrCreateAsync(persistentPlayerId, name);

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            try
            {
                int? opponentId = null;
                await using (var findCmd = new NpgsqlCommand(
                    "SELECT id FROM players WHERE status = 'waiting' " +
                    "ORDER BY ABS(elo_at_join - @myElo) ASC, created_at ASC " +
                    "LIMIT 1 FOR UPDATE SKIP LOCKED",
                    connection, tx))
                {
                    findCmd.Parameters.AddWithValue("myElo", elo);
                    var found = await findCmd.ExecuteScalarAsync();
                    if (found != null) opponentId = (int)found;
                }

                if (opponentId == null)
                {
                    await using var insertCmd = new NpgsqlCommand(
                        "INSERT INTO players (name, status, persistent_player_id, elo_at_join) " +
                        "VALUES (@name, 'waiting', @pid, @elo) RETURNING id",
                        connection, tx);
                    insertCmd.Parameters.AddWithValue("name", resolvedName);
                    insertCmd.Parameters.AddWithValue("pid", resolvedId);
                    insertCmd.Parameters.AddWithValue("elo", elo);
                    var newId = (int)(await insertCmd.ExecuteScalarAsync())!;

                    await tx.CommitAsync();
                    return new JoinResult
                    {
                        Matched = false,
                        PlayerId = newId,
                        PersistentPlayerId = resolvedId,
                        MyElo = elo
                    };
                }

                // Grab the opponent's display info before their row changes.
                string opponentName;
                int opponentElo;
                await using (var opponentInfoCmd = new NpgsqlCommand(
                    "SELECT name, elo_at_join FROM players WHERE id = @id", connection, tx))
                {
                    opponentInfoCmd.Parameters.AddWithValue("id", opponentId.Value);
                    await using var reader = await opponentInfoCmd.ExecuteReaderAsync();
                    await reader.ReadAsync();
                    opponentName = reader.GetString(0);
                    opponentElo = reader.GetInt32(1);
                }

                await using var matchCmd = new NpgsqlCommand(
                    "INSERT INTO matches (player1_id, status) VALUES (@p1, 'active') RETURNING id",
                    connection, tx);
                matchCmd.Parameters.AddWithValue("p1", opponentId.Value);
                var matchId = (int)(await matchCmd.ExecuteScalarAsync())!;

                await using var insertSelfCmd = new NpgsqlCommand(
                    "INSERT INTO players (name, status, match_id, persistent_player_id, elo_at_join) " +
                    "VALUES (@name, 'in_match', @matchId, @pid, @elo) RETURNING id",
                    connection, tx);
                insertSelfCmd.Parameters.AddWithValue("name", resolvedName);
                insertSelfCmd.Parameters.AddWithValue("matchId", matchId);
                insertSelfCmd.Parameters.AddWithValue("pid", resolvedId);
                insertSelfCmd.Parameters.AddWithValue("elo", elo);
                var selfId = (int)(await insertSelfCmd.ExecuteScalarAsync())!;

                await using (var updateOpponentCmd = new NpgsqlCommand(
                    "UPDATE players SET status = 'in_match', match_id = @matchId WHERE id = @id",
                    connection, tx))
                {
                    updateOpponentCmd.Parameters.AddWithValue("matchId", matchId);
                    updateOpponentCmd.Parameters.AddWithValue("id", opponentId.Value);
                    await updateOpponentCmd.ExecuteNonQueryAsync();
                }

                await using (var updateMatchCmd = new NpgsqlCommand(
                    "UPDATE matches SET player2_id = @p2 WHERE id = @matchId",
                    connection, tx))
                {
                    updateMatchCmd.Parameters.AddWithValue("p2", selfId);
                    updateMatchCmd.Parameters.AddWithValue("matchId", matchId);
                    await updateMatchCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return new JoinResult
                {
                    Matched = true,
                    PlayerId = selfId,
                    MatchId = matchId,
                    PersistentPlayerId = resolvedId,
                    MyElo = elo,
                    OpponentName = opponentName,
                    OpponentElo = opponentElo
                };
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // The waiting player's client polls this until Matched becomes true,
        // at which point OpponentName/OpponentElo are also populated.
        public async Task<JoinResult> GetMatchStatusForWaitingPlayerAsync(int playerId)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();

            string status;
            int? matchId;
            string persistentId;
            int myElo;

            await using (var cmd = new NpgsqlCommand(
                "SELECT status, match_id, persistent_player_id, elo_at_join FROM players WHERE id = @id",
                connection))
            {
                cmd.Parameters.AddWithValue("id", playerId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    throw new KeyNotFoundException($"Player {playerId} not found.");

                status = reader.GetString(0);
                matchId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                persistentId = reader.GetString(2);
                myElo = reader.GetInt32(3);
            }

            var result = new JoinResult
            {
                Matched = status != "waiting",
                PlayerId = playerId,
                MatchId = matchId,
                PersistentPlayerId = persistentId,
                MyElo = myElo
            };

            if (result.Matched && matchId != null)
            {
                await using var opponentCmd = new NpgsqlCommand(
                    "SELECT name, elo_at_join FROM players WHERE match_id = @matchId AND id != @selfId",
                    connection);
                opponentCmd.Parameters.AddWithValue("matchId", matchId.Value);
                opponentCmd.Parameters.AddWithValue("selfId", playerId);
                await using var reader = await opponentCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result.OpponentName = reader.GetString(0);
                    result.OpponentElo = reader.GetInt32(1);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------
        // Scoring
        // ---------------------------------------------------------------

        // Marks this player finished. Once both players in the match are
        // finished, decides the winner, settles Elo for both (guarded so a
        // retried request can't double-apply it), and writes the new
        // ratings back to Redis.
        public async Task SubmitScoreAsync(int playerId, int correctCount, int totalTimeMs)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            (string PersistentId, int NewElo)? p1EloUpdate = null;
            (string PersistentId, int NewElo)? p2EloUpdate = null;

            try
            {
                await using (var updateCmd = new NpgsqlCommand(
                    "UPDATE players SET status = 'finished', correct_count = @c, total_time_ms = @t WHERE id = @id",
                    connection, tx))
                {
                    updateCmd.Parameters.AddWithValue("c", correctCount);
                    updateCmd.Parameters.AddWithValue("t", totalTimeMs);
                    updateCmd.Parameters.AddWithValue("id", playerId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                int? matchId = null;
                await using (var findMatchCmd = new NpgsqlCommand(
                    "SELECT match_id FROM players WHERE id = @id", connection, tx))
                {
                    findMatchCmd.Parameters.AddWithValue("id", playerId);
                    var val = await findMatchCmd.ExecuteScalarAsync();
                    if (val != null && val != DBNull.Value) matchId = (int)val;
                }

                if (matchId == null)
                {
                    await tx.CommitAsync();
                    return;
                }

                // Lock the match row and bail if it's already settled - this
                // stops a retried/duplicate request from re-awarding Elo.
                string matchStatus;
                await using (var matchStatusCmd = new NpgsqlCommand(
                    "SELECT status FROM matches WHERE id = @matchId FOR UPDATE", connection, tx))
                {
                    matchStatusCmd.Parameters.AddWithValue("matchId", matchId.Value);
                    matchStatus = (string)(await matchStatusCmd.ExecuteScalarAsync())!;
                }
                if (matchStatus == "completed")
                {
                    await tx.CommitAsync();
                    return;
                }

                var players = new List<(int Id, string Status, int Correct, int TimeMs, string PersistentId, int EloAtJoin)>();
                await using (var playersCmd = new NpgsqlCommand(
                    "SELECT id, status, correct_count, total_time_ms, persistent_player_id, elo_at_join " +
                    "FROM players WHERE match_id = @matchId",
                    connection, tx))
                {
                    playersCmd.Parameters.AddWithValue("matchId", matchId.Value);
                    await using var reader = await playersCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        players.Add((
                            reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2),
                            reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5)));
                    }
                }

                if (players.Count == 2 && players.All(p => p.Status == "finished"))
                {
                    var p1 = players[0];
                    var p2 = players[1];

                    int? winnerId;
                    bool isTie;
                    if (p1.Correct != p2.Correct)
                    {
                        winnerId = p1.Correct > p2.Correct ? p1.Id : p2.Id;
                        isTie = false;
                    }
                    else if (p1.TimeMs != p2.TimeMs)
                    {
                        winnerId = p1.TimeMs < p2.TimeMs ? p1.Id : p2.Id;
                        isTie = false;
                    }
                    else
                    {
                        winnerId = null;
                        isTie = true;
                    }

                    var p1Outcome = isTie ? EloOutcome.Tie : (winnerId == p1.Id ? EloOutcome.Win : EloOutcome.Loss);
                    var p2Outcome = isTie ? EloOutcome.Tie : (winnerId == p2.Id ? EloOutcome.Win : EloOutcome.Loss);

                    var p1NewElo = EloCalculator.CalculateNewRating(p1.EloAtJoin, p2.EloAtJoin, p1Outcome);
                    var p2NewElo = EloCalculator.CalculateNewRating(p2.EloAtJoin, p1.EloAtJoin, p2Outcome);

                    await using (var completeCmd = new NpgsqlCommand(
                        "UPDATE matches SET status = 'completed', winner_id = @winner, is_tie = @tie, completed_at = now() WHERE id = @matchId",
                        connection, tx))
                    {
                        completeCmd.Parameters.AddWithValue("winner", (object?)winnerId ?? DBNull.Value);
                        completeCmd.Parameters.AddWithValue("tie", isTie);
                        completeCmd.Parameters.AddWithValue("matchId", matchId.Value);
                        await completeCmd.ExecuteNonQueryAsync();
                    }

                    await using (var eloCmd1 = new NpgsqlCommand(
                        "UPDATE players SET elo_change = @change WHERE id = @id", connection, tx))
                    {
                        eloCmd1.Parameters.AddWithValue("change", p1NewElo - p1.EloAtJoin);
                        eloCmd1.Parameters.AddWithValue("id", p1.Id);
                        await eloCmd1.ExecuteNonQueryAsync();
                    }
                    await using (var eloCmd2 = new NpgsqlCommand(
                        "UPDATE players SET elo_change = @change WHERE id = @id", connection, tx))
                    {
                        eloCmd2.Parameters.AddWithValue("change", p2NewElo - p2.EloAtJoin);
                        eloCmd2.Parameters.AddWithValue("id", p2.Id);
                        await eloCmd2.ExecuteNonQueryAsync();
                    }

                    p1EloUpdate = (p1.PersistentId, p1NewElo);
                    p2EloUpdate = (p2.PersistentId, p2NewElo);
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            // Redis writes happen after the Postgres transaction commits,
            // once the match is durably marked completed.
            if (p1EloUpdate != null) await _profiles.UpdateEloAsync(p1EloUpdate.Value.PersistentId, p1EloUpdate.Value.NewElo);
            if (p2EloUpdate != null) await _profiles.UpdateEloAsync(p2EloUpdate.Value.PersistentId, p2EloUpdate.Value.NewElo);
        }

        // Both clients poll this after submitting their own score, until
        // Completed == true, then read WinnerId / IsTie / Players (including
        // each player's EloChange/NewElo) to show the result screen.
        public async Task<MatchResult?> GetMatchResultAsync(int playerId)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();

            int matchId;
            await using (var matchIdCmd = new NpgsqlCommand(
                "SELECT match_id FROM players WHERE id = @id", connection))
            {
                matchIdCmd.Parameters.AddWithValue("id", playerId);
                var matchIdObj = await matchIdCmd.ExecuteScalarAsync();
                if (matchIdObj == null || matchIdObj == DBNull.Value) return null;
                matchId = (int)matchIdObj;
            }

            string matchStatus = "active";
            int? winnerId = null;
            bool isTie = false;
            var playerResults = new List<PlayerResult>();

            await using (var cmd = new NpgsqlCommand(
                @"SELECT m.status, m.winner_id, m.is_tie,
                         p.id, p.name, p.correct_count, p.total_time_ms, p.elo_at_join, p.elo_change
                  FROM matches m
                  JOIN players p ON p.match_id = m.id
                  WHERE m.id = @matchId",
                connection))
            {
                cmd.Parameters.AddWithValue("matchId", matchId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    matchStatus = reader.GetString(0);
                    winnerId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    isTie = reader.GetBoolean(2);

                    var eloAtJoin = reader.GetInt32(7);
                    int? eloChange = reader.IsDBNull(8) ? null : reader.GetInt32(8);

                    playerResults.Add(new PlayerResult
                    {
                        PlayerId = reader.GetInt32(3),
                        Name = reader.GetString(4),
                        CorrectCount = reader.GetInt32(5),
                        TotalTimeMs = reader.GetInt32(6),
                        EloChange = eloChange,
                        NewElo = eloChange.HasValue ? eloAtJoin + eloChange.Value : null
                    });
                }
            }

            return new MatchResult
            {
                MatchId = matchId,
                Completed = matchStatus == "completed",
                WinnerId = winnerId,
                IsTie = isTie,
                Players = playerResults
            };
        }
    }
}
