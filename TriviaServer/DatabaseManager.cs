using Npgsql;
using TriviaServer.Models;

namespace TriviaServer
{
    public class DatabaseManager
    {
        private readonly string _connectionString;

        public DatabaseManager(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("SupabaseDb")
                ?? throw new InvalidOperationException(
                    "Connection string 'SupabaseDb' is missing. Set it via user-secrets or an environment variable, see SETUP.md.");
        }

        private NpgsqlConnection CreateConnection() => new(_connectionString);

        // ---------------------------------------------------------------
        // Questions
        // ---------------------------------------------------------------

        // No LIMIT here on purpose: edit or add rows in the "Questions" table
        // in Supabase and they show up next time the Unity client fetches
        // this endpoint, with no rebuild needed.
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

        // Called when a player opens the game and wants to play.
        // If someone is already waiting, this pairs them up immediately and
        // both are marked in_match. Otherwise the caller becomes the one
        // waiting player, and their client should poll GetMatchStatusForWaitingPlayerAsync.
        public async Task<JoinResult> JoinMatchAsync(string name)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            try
            {
                // FOR UPDATE SKIP LOCKED so two players joining at the exact
                // same moment can't both grab the same waiting opponent.
                int? opponentId = null;
                await using (var findCmd = new NpgsqlCommand(
                    "SELECT id FROM players WHERE status = 'waiting' " +
                    "ORDER BY created_at ASC LIMIT 1 FOR UPDATE SKIP LOCKED",
                    connection, tx))
                {
                    var found = await findCmd.ExecuteScalarAsync();
                    if (found != null) opponentId = (int)found;
                }

                if (opponentId == null)
                {
                    await using var insertCmd = new NpgsqlCommand(
                        "INSERT INTO players (name, status) VALUES (@name, 'waiting') RETURNING id",
                        connection, tx);
                    insertCmd.Parameters.AddWithValue("name", name);
                    var newId = (int)(await insertCmd.ExecuteScalarAsync())!;

                    await tx.CommitAsync();
                    return new JoinResult { Matched = false, PlayerId = newId };
                }

                await using var matchCmd = new NpgsqlCommand(
                    "INSERT INTO matches (player1_id, status) VALUES (@p1, 'active') RETURNING id",
                    connection, tx);
                matchCmd.Parameters.AddWithValue("p1", opponentId.Value);
                var matchId = (int)(await matchCmd.ExecuteScalarAsync())!;

                await using var insertSelfCmd = new NpgsqlCommand(
                    "INSERT INTO players (name, status, match_id) VALUES (@name, 'in_match', @matchId) RETURNING id",
                    connection, tx);
                insertSelfCmd.Parameters.AddWithValue("name", name);
                insertSelfCmd.Parameters.AddWithValue("matchId", matchId);
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
                return new JoinResult { Matched = true, PlayerId = selfId, MatchId = matchId };
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // The waiting player's client calls this every second or two until
        // Matched becomes true.
        public async Task<JoinResult> GetMatchStatusForWaitingPlayerAsync(int playerId)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT status, match_id FROM players WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("id", playerId);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
                throw new KeyNotFoundException($"Player {playerId} not found.");

            var status = reader.GetString(0);
            var matchId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);

            return new JoinResult
            {
                Matched = status != "waiting",
                PlayerId = playerId,
                MatchId = matchId
            };
        }

        // ---------------------------------------------------------------
        // Scoring
        // ---------------------------------------------------------------

        // Called once, when a player finishes all their questions. Marks
        // that player finished, and if the opponent is also finished,
        // decides the winner right here (correct count, then total time,
        // then tie) and closes out the match.
        public async Task SubmitScoreAsync(int playerId, int correctCount, int totalTimeMs)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

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

                if (matchId != null)
                {
                    var players = new List<(int Id, string Status, int Correct, int TimeMs)>();
                    await using (var playersCmd = new NpgsqlCommand(
                        "SELECT id, status, correct_count, total_time_ms FROM players WHERE match_id = @matchId",
                        connection, tx))
                    {
                        playersCmd.Parameters.AddWithValue("matchId", matchId.Value);
                        await using var reader = await playersCmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            players.Add((
                                reader.GetInt32(0),
                                reader.GetString(1),
                                reader.GetInt32(2),
                                reader.GetInt32(3)));
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
                            // Same correct count -> the bonus tiebreak: faster total time wins.
                            winnerId = p1.TimeMs < p2.TimeMs ? p1.Id : p2.Id;
                            isTie = false;
                        }
                        else
                        {
                            winnerId = null;
                            isTie = true;
                        }

                        await using var completeCmd = new NpgsqlCommand(
                            "UPDATE matches SET status = 'completed', winner_id = @winner, is_tie = @tie, completed_at = now() WHERE id = @matchId",
                            connection, tx);
                        completeCmd.Parameters.AddWithValue("winner", (object?)winnerId ?? DBNull.Value);
                        completeCmd.Parameters.AddWithValue("tie", isTie);
                        completeCmd.Parameters.AddWithValue("matchId", matchId.Value);
                        await completeCmd.ExecuteNonQueryAsync();
                    }
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // Both clients poll this after submitting their own score, until
        // Completed == true, then read WinnerId / IsTie / Players to show
        // the result screen.
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
                         p.id, p.name, p.correct_count, p.total_time_ms
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

                    playerResults.Add(new PlayerResult
                    {
                        PlayerId = reader.GetInt32(3),
                        Name = reader.GetString(4),
                        CorrectCount = reader.GetInt32(5),
                        TotalTimeMs = reader.GetInt32(6)
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
