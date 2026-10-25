using System.Data;
using Microsoft.Data.SqlClient;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>Writes dbo.TestManifest. Test data only; the load never reads it.</summary>
public sealed class TestManifestRepository
{
	private readonly SqlConnectionFactory _connectionFactory;

	public TestManifestRepository(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	public async Task WriteManifestAsync(IReadOnlyList<ManifestEntry> entries, CancellationToken cancellationToken)
	{
		DataTable table = new DataTable("dbo.TestManifest");
		table.Columns.Add("MessageId", typeof(string));
		table.Columns.Add("ArrivalNo", typeof(int));
		table.Columns.Add("MessageType", typeof(string));
		table.Columns.Add("FaultKind", typeof(string));
		table.Columns.Add("ExpectedRowCount", typeof(int));

		foreach (ManifestEntry entry in entries)
		{
			table.Rows.Add(entry.MessageId, entry.ArrivalNo, entry.MessageType, entry.FaultKind, entry.ExpectedRowCount);
		}

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		using SqlBulkCopy bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null);
		bulkCopy.DestinationTableName = table.TableName;
		bulkCopy.BulkCopyTimeout = SqlConnectionFactory.LONG_COMMAND_TIMEOUT_SECONDS;

		foreach (DataColumn column in table.Columns)
		{
			bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
		}

		await bulkCopy.WriteToServerAsync(table, cancellationToken);
	}
}
