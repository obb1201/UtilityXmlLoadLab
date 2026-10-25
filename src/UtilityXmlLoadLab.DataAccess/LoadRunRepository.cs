using System.Data;
using Microsoft.Data.SqlClient;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>Writes dbo.LoadRun / dbo.LoadRunBatch and runs the approach A procedures.</summary>
public sealed class LoadRunRepository
{
	private readonly SqlConnectionFactory _connectionFactory;

	public LoadRunRepository(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	public async Task<int> StartRunAsync(char approach, int degreeOfParallelism, string note, CancellationToken cancellationToken)
	{
		const string SQL = @"
INSERT INTO dbo.LoadRun (Approach, DegreeOfParallelism, StartedAt, Note)
VALUES (@Approach, @Dop, SYSUTCDATETIME(), @Note);
SELECT CAST(SCOPE_IDENTITY() AS int);";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		command.Parameters.Add("@Approach", SqlDbType.Char, 1).Value = approach.ToString();
		command.Parameters.Add("@Dop", SqlDbType.Int).Value = degreeOfParallelism;
		command.Parameters.Add("@Note", SqlDbType.NVarChar, 200).Value = note;
		return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
	}

	public async Task LogBatchAsync(int runId, int batchId, DateTime startedAt, DateTime endedAt, string? errorMessageOrNull,
		CancellationToken cancellationToken)
	{
		const string SQL = @"
INSERT INTO dbo.LoadRunBatch (RunId, BatchId, StartedAt, EndedAt, IsSucceeded, ErrorMessage)
VALUES (@RunId, @BatchId, @StartedAt, @EndedAt, @IsSucceeded, @ErrorMessage);";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		command.Parameters.Add("@RunId", SqlDbType.Int).Value = runId;
		command.Parameters.Add("@BatchId", SqlDbType.Int).Value = batchId;
		command.Parameters.Add("@StartedAt", SqlDbType.DateTime2).Value = startedAt;
		command.Parameters.Add("@EndedAt", SqlDbType.DateTime2).Value = endedAt;
		command.Parameters.Add("@IsSucceeded", SqlDbType.Bit).Value = errorMessageOrNull == null;
		command.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, 4000).Value = (object?)errorMessageOrNull ?? DBNull.Value;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task EndRunAsync(int runId, int batchCount, int failedBatchCount, CancellationToken cancellationToken)
	{
		const string SQL = @"
UPDATE dbo.LoadRun
SET EndedAt = SYSUTCDATETIME(), BatchCount = @BatchCount, FailedBatchCount = @FailedBatchCount
WHERE RunId = @RunId;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		command.Parameters.Add("@RunId", SqlDbType.Int).Value = runId;
		command.Parameters.Add("@BatchCount", SqlDbType.Int).Value = batchCount;
		command.Parameters.Add("@FailedBatchCount", SqlDbType.Int).Value = failedBatchCount;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	/// <summary>Approach A, one batch: EXEC dbo.usp_ShredBatch (it has its own transaction).</summary>
	public async Task ShredBatchAsync(int batchId, CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_ShredBatch");
		command.Parameters.Add("@BatchId", SqlDbType.Int).Value = batchId;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	/// <summary>Approach A, serial driver: EXEC dbo.usp_RunShredDriver (the SQL Agent-style loop).</summary>
	/// <returns>The run summary row (RunId, BatchCount, FailedBatchCount, ElapsedMs, ...).</returns>
	public async Task<DataTable> RunShredDriverAsync(bool bOnlyPending, string note, CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_RunShredDriver");
		command.CommandTimeout = 0;
		command.Parameters.Add("@OnlyPending", SqlDbType.Bit).Value = bOnlyPending;
		command.Parameters.Add("@Note", SqlDbType.NVarChar, 200).Value = note;

		IReadOnlyList<DataTable> tables = await ResultSetReader.ReadAllResultSetsAsync(command, cancellationToken);
		return tables[tables.Count - 1];
	}

	/// <summary>Failed batches of a run, with their error message.</summary>
	public async Task<DataTable> GetFailedBatchesAsync(int runId, CancellationToken cancellationToken)
	{
		const string SQL = "SELECT BatchId, ErrorMessage FROM dbo.LoadRunBatch WHERE RunId = @RunId AND IsSucceeded = 0 ORDER BY BatchId;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		command.Parameters.Add("@RunId", SqlDbType.Int).Value = runId;
		IReadOnlyList<DataTable> tables = await ResultSetReader.ReadAllResultSetsAsync(command, cancellationToken);
		return tables[0];
	}
}
