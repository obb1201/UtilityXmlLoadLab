using System.Data;
using Microsoft.Data.SqlClient;
using UtilityXmlLoadLab.Core.Messages;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>Reads and writes dbo.Inbox. No XML parsing happens here.</summary>
public sealed class InboxRepository
{
	private readonly SqlConnectionFactory _connectionFactory;

	public InboxRepository(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	/// <summary>Stores one message (used by the CoreWCF receiver).</summary>
	/// <returns>"Accepted", or "Duplicate" when the MessageId was already in the Inbox.</returns>
	public async Task<string> ReceiveMessageAsync(string messageId, string rawXml, CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_ReceiveMessage");
		command.Parameters.Add("@MessageId", SqlDbType.VarChar, 50).Value = messageId;
		command.Parameters.Add("@RawXml", SqlDbType.NVarChar, -1).Value = rawXml;
		SqlParameter outcome = command.Parameters.Add("@Outcome", SqlDbType.VarChar, 20);
		outcome.Direction = ParameterDirection.Output;

		await command.ExecuteNonQueryAsync(cancellationToken);
		return (string)outcome.Value;
	}

	/// <summary>
	/// The fast "direct insert" path: bulk-copies a chunk into a temp landing table, then lets
	/// dbo.usp_ReceiveLanding apply the same duplicate rules as the receiver.
	/// </summary>
	public async Task<(int AcceptedCount, int DuplicateCount)> ReceiveBulkAsync(
		IReadOnlyList<(string MessageId, string RawXml)> messages, CancellationToken cancellationToken)
	{
		DataTable landing = new DataTable();
		landing.Columns.Add("LandingSeq", typeof(int));
		landing.Columns.Add("MessageId", typeof(string));
		landing.Columns.Add("RawXml", typeof(string));

		for (int index = 0; index < messages.Count; index++)
		{
			landing.Rows.Add(index + 1, messages[index].MessageId, messages[index].RawXml);
		}

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);

		await using (SqlCommand create = SqlConnectionFactory.CreateTextCommand(connection,
			"CREATE TABLE #InboxLanding (LandingSeq int NOT NULL PRIMARY KEY, MessageId varchar(50) NOT NULL, RawXml nvarchar(max) NOT NULL);"))
		{
			await create.ExecuteNonQueryAsync(cancellationToken);
		}

		using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
		{
			bulkCopy.DestinationTableName = "#InboxLanding";
			bulkCopy.BulkCopyTimeout = SqlConnectionFactory.LONG_COMMAND_TIMEOUT_SECONDS;
			await bulkCopy.WriteToServerAsync(landing, cancellationToken);
		}

		await using SqlCommand receive = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_ReceiveLanding");
		await using SqlDataReader reader = await receive.ExecuteReaderAsync(cancellationToken);
		await reader.ReadAsync(cancellationToken);
		return (reader.GetInt32(0), reader.GetInt32(1));
	}

	/// <summary>Puts every unbatched message into a batch of <paramref name="batchSize"/> messages.</summary>
	/// <returns>The number of new batches.</returns>
	public async Task<int> CreateBatchesAsync(int batchSize, CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_CreateBatches");
		command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;

		IReadOnlyList<DataTable> tables = await ResultSetReader.ReadAllResultSetsAsync(command, cancellationToken);
		return tables.Count == 0 ? 0 : tables[0].Rows.Count;
	}

	/// <summary>Batch ids in the Inbox. With <paramref name="bOnlyPending"/>, only batches not yet staged.</summary>
	public async Task<IReadOnlyList<int>> GetBatchIdsAsync(bool bOnlyPending, CancellationToken cancellationToken)
	{
		const string SQL = @"
SELECT DISTINCT i.BatchId
FROM dbo.Inbox i
LEFT JOIN dbo.LoadEvidence_Batch b ON b.BatchId = i.BatchId
WHERE i.BatchId IS NOT NULL
  AND (b.BatchId IS NULL OR (@OnlyPending = 0 AND b.Status = 'Staged'))
ORDER BY i.BatchId;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		command.Parameters.Add("@OnlyPending", SqlDbType.Bit).Value = bOnlyPending;

		List<int> batchIds = new List<int>();
		await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			batchIds.Add(reader.GetInt32(0));
		}

		return batchIds;
	}

	public async Task<IReadOnlyList<RawMessage>> GetBatchMessagesAsync(int batchId, CancellationToken cancellationToken)
	{
		const string SQL = "SELECT MessageId, ReceivedAt, RawXml FROM dbo.Inbox WHERE BatchId = @BatchId ORDER BY ArrivalSeq;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		command.Parameters.Add("@BatchId", SqlDbType.Int).Value = batchId;

		List<RawMessage> messages = new List<RawMessage>();
		await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			messages.Add(new RawMessage(reader.GetString(0), reader.GetDateTime(1), reader.GetString(2)));
		}

		return messages;
	}

	/// <summary>Counts for reports: messages, duplicate arrivals, batches.</summary>
	public async Task<(long MessageCount, long DuplicateCount, int BatchCount)> GetInboxCountsAsync(CancellationToken cancellationToken)
	{
		const string SQL = @"
SELECT (SELECT COUNT_BIG(*) FROM dbo.Inbox),
       (SELECT COUNT_BIG(*) FROM dbo.InboxDuplicate),
       (SELECT COUNT(DISTINCT BatchId) FROM dbo.Inbox WHERE BatchId IS NOT NULL);";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		await reader.ReadAsync(cancellationToken);
		return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
	}
}
