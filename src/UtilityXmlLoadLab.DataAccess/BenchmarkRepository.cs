using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using UtilityXmlLoadLab.Core.Mapping;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>Resets staging between benchmark runs and fingerprints the output so runs can be compared.</summary>
public sealed class BenchmarkRepository
{
	private readonly SqlConnectionFactory _connectionFactory;

	public BenchmarkRepository(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	/// <summary>Empties staging and evidence. Inbox and batches stay.</summary>
	public async Task ResetStagingAsync(bool bAlsoResetTarget, CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_ResetStaging");
		command.Parameters.Add("@AlsoResetTarget", SqlDbType.Bit).Value = bAlsoResetTarget;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	/// <summary>Empties everything, including Inbox and target. For a fresh generated data set.</summary>
	public async Task ResetAllAsync(CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_ResetAll");
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	/// <summary>
	/// One line per staging and evidence table: row count and a content checksum. Two runs with
	/// identical fingerprints wrote the same data. Free-text columns (ErrorDetail) and timestamps
	/// are left out on purpose; the approaches may word a parse error differently.
	/// </summary>
	public async Task<DataTable> GetFingerprintAsync(CancellationToken cancellationToken)
	{
		StringBuilder sql = new StringBuilder();
		int sortOrder = 0;

		foreach (StagingTable table in StagingMap.TABLES)
		{
			sortOrder++;
			sql.Append("SELECT ").Append(sortOrder).Append(" AS SortOrder, '").Append(table.QualifiedName).Append("' AS TableName, ")
				.Append("COUNT_BIG(*) AS RowCnt, ISNULL(SUM(CAST(BINARY_CHECKSUM(*) AS bigint)), 0) AS ContentChecksum FROM ")
				.Append(table.QualifiedName).AppendLine(" UNION ALL");
		}

		sql.AppendLine("SELECT 98, 'dbo.LoadEvidence_Message', COUNT_BIG(*), ISNULL(SUM(CAST(BINARY_CHECKSUM(MessageId, ArrivalNo, BatchId, "
			+ "MessageType, RawXmlHash, ExpectedRowCount, StagedRowCount, ValidationStatus) AS bigint)), 0) FROM dbo.LoadEvidence_Message UNION ALL");
		sql.AppendLine("SELECT 99, 'dbo.LoadEvidence_Batch', COUNT_BIG(*), ISNULL(SUM(CAST(BINARY_CHECKSUM(BatchId, MessageCount, PassedCount, "
			+ "FailedCount, DuplicateCount, StagedRowTotal) AS bigint)), 0) FROM dbo.LoadEvidence_Batch");
		sql.AppendLine("ORDER BY SortOrder;");

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, sql.ToString());
		IReadOnlyList<DataTable> tables = await ResultSetReader.ReadAllResultSetsAsync(command, cancellationToken);
		return tables[0];
	}

	/// <summary>Totals of the staged output: passed/failed/duplicate arrivals and staged rows.</summary>
	public async Task<(long PassedCount, long FailedCount, long DuplicateCount, long StagedRows, int StagedBatches)> GetStagedTotalsAsync(
		CancellationToken cancellationToken)
	{
		const string SQL = @"
SELECT ISNULL(SUM(CAST(PassedCount AS bigint)), 0), ISNULL(SUM(CAST(FailedCount AS bigint)), 0),
       ISNULL(SUM(CAST(DuplicateCount AS bigint)), 0), ISNULL(SUM(CAST(StagedRowTotal AS bigint)), 0), COUNT(*)
FROM dbo.LoadEvidence_Batch;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		await reader.ReadAsync(cancellationToken);
		return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4));
	}

	/// <summary>SQL Server version, CPU count and memory, for the benchmark report.</summary>
	public async Task<string> GetServerDescriptionAsync(CancellationToken cancellationToken)
	{
		const string SQL = @"
SELECT CONCAT('SQL Server ', CAST(SERVERPROPERTY('ProductVersion') AS varchar(30)), ' ',
              CAST(SERVERPROPERTY('Edition') AS varchar(60)), ', ', cpu_count, ' logical CPUs, ',
              physical_memory_kb / 1024, ' MB memory')
FROM sys.dm_os_sys_info;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
	}
}
