using System.Data;
using Microsoft.Data.SqlClient;
using UtilityXmlLoadLab.Core.Mapping;
using UtilityXmlLoadLab.Core.Messages;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>
/// Approach B writer. In ONE transaction per batch: usp_BeginBatch, SqlBulkCopy of every staging
/// table and of the message evidence, then the shared usp_FinalizeBatch (same as approach A).
/// </summary>
public sealed class StagingWriter
{
	private readonly SqlConnectionFactory _connectionFactory;

	public StagingWriter(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	public async Task WriteBatchAsync(int batchId, IReadOnlyList<MessageResult> results, CancellationToken cancellationToken)
	{
		Dictionary<StagingTable, DataTable> stagingTables = StagingMap.TABLES.ToDictionary(table => table, CreateStagingDataTable);
		DataTable evidence = CreateEvidenceDataTable();
		DateTime processedAt = DateTime.UtcNow;

		foreach (MessageResult result in results)
		{
			foreach (StagedRow row in result.Rows)
			{
				DataTable target = stagingTables[row.Table];
				object?[] values = new object?[row.Values.Length + 2];
				values[0] = result.MessageId;
				values[1] = batchId;
				Array.Copy(row.Values, 0, values, 2, row.Values.Length);

				for (int index = 0; index < values.Length; index++)
				{
					values[index] ??= DBNull.Value;
				}

				target.Rows.Add(values);
			}

			evidence.Rows.Add(
				result.MessageId,
				1,
				batchId,
				(object?)result.MessageType ?? DBNull.Value,
				result.ReceivedAt,
				result.RawXmlHash,
				result.ExpectedRowCount.HasValue ? result.ExpectedRowCount.Value : DBNull.Value,
				0,
				result.Status.ToString(),
				(object?)result.ErrorDetail ?? DBNull.Value,
				processedAt,
				"B");
		}

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

		await using (SqlCommand begin = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_BeginBatch"))
		{
			begin.Transaction = transaction;
			begin.Parameters.Add("@BatchId", SqlDbType.Int).Value = batchId;
			await begin.ExecuteNonQueryAsync(cancellationToken);
		}

		foreach (DataTable table in stagingTables.Values.Where(table => table.Rows.Count > 0))
		{
			await BulkCopyAsync(connection, transaction, table, SqlBulkCopyOptions.Default, cancellationToken);
		}

		// CheckConstraints keeps the evidence table's CHECK constraints trusted.
		await BulkCopyAsync(connection, transaction, evidence, SqlBulkCopyOptions.CheckConstraints, cancellationToken);

		await using (SqlCommand finalize = SqlConnectionFactory.CreateProcedureCommand(connection, "dbo.usp_FinalizeBatch"))
		{
			finalize.Transaction = transaction;
			finalize.Parameters.Add("@BatchId", SqlDbType.Int).Value = batchId;
			finalize.Parameters.Add("@Approach", SqlDbType.Char, 1).Value = "B";
			await finalize.ExecuteNonQueryAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);
	}

	private static async Task BulkCopyAsync(SqlConnection connection, SqlTransaction transaction, DataTable table,
		SqlBulkCopyOptions options, CancellationToken cancellationToken)
	{
		using SqlBulkCopy bulkCopy = new SqlBulkCopy(connection, options, transaction);
		bulkCopy.DestinationTableName = table.TableName;
		bulkCopy.BulkCopyTimeout = SqlConnectionFactory.LONG_COMMAND_TIMEOUT_SECONDS;

		foreach (DataColumn column in table.Columns)
		{
			bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
		}

		await bulkCopy.WriteToServerAsync(table, cancellationToken);
	}

	private static DataTable CreateStagingDataTable(StagingTable stagingTable)
	{
		DataTable table = new DataTable(stagingTable.QualifiedName);
		table.Columns.Add("MessageId", typeof(string));
		table.Columns.Add("BatchId", typeof(int));

		foreach (StagingColumn column in stagingTable.Columns)
		{
			table.Columns.Add(column.Name, GetClrType(column.ValueType));
		}

		return table;
	}

	private static DataTable CreateEvidenceDataTable()
	{
		DataTable table = new DataTable("dbo.LoadEvidence_Message");
		table.Columns.Add("MessageId", typeof(string));
		table.Columns.Add("ArrivalNo", typeof(int));
		table.Columns.Add("BatchId", typeof(int));
		table.Columns.Add("MessageType", typeof(string));
		table.Columns.Add("ReceivedAt", typeof(DateTime));
		table.Columns.Add("RawXmlHash", typeof(byte[]));
		table.Columns.Add("ExpectedRowCount", typeof(int));
		table.Columns.Add("StagedRowCount", typeof(int));
		table.Columns.Add("ValidationStatus", typeof(string));
		table.Columns.Add("ErrorDetail", typeof(string));
		table.Columns.Add("ProcessedAt", typeof(DateTime));
		table.Columns.Add("Approach", typeof(string));
		return table;
	}

	private static Type GetClrType(EValueType valueType)
	{
		switch (valueType)
		{
			case EValueType.Text:
				return typeof(string);
			case EValueType.Date:
			case EValueType.DateTime:
				return typeof(DateTime);
			case EValueType.Decimal18x2:
			case EValueType.Decimal5x2:
				return typeof(decimal);
			case EValueType.Integer:
				return typeof(int);
			default:
				System.Diagnostics.Debug.Fail("Unknown value type " + valueType);
				return typeof(string);
		}
	}
}
