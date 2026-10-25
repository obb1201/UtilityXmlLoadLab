using System.Data;
using Microsoft.Data.SqlClient;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>The promotion gate: reference checks, approve, reject, promote, and reports.</summary>
public sealed class GateRepository
{
	private readonly SqlConnectionFactory _connectionFactory;

	public GateRepository(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	public Task<IReadOnlyList<DataTable>> CheckReferencesAsync(CancellationToken cancellationToken)
	{
		return ExecuteProcedureAsync("dbo.usp_CheckReferences", Array.Empty<SqlParameter>(), cancellationToken);
	}

	public Task<IReadOnlyList<DataTable>> ApproveBatchAsync(int batchId, string approvedBy, CancellationToken cancellationToken)
	{
		SqlParameter[] parameters =
		{
			CreateIntParameter("@BatchId", batchId),
			CreateTextParameter("@ApprovedBy", approvedBy, 100),
			CreateBitParameter("@AutoApprove", false)
		};
		return ExecuteProcedureAsync("dbo.usp_ApproveBatch", parameters, cancellationToken);
	}

	public Task<IReadOnlyList<DataTable>> AutoApproveBatchesAsync(string approvedBy, CancellationToken cancellationToken)
	{
		SqlParameter[] parameters = { CreateTextParameter("@ApprovedBy", approvedBy, 100) };
		return ExecuteProcedureAsync("dbo.usp_AutoApproveBatches", parameters, cancellationToken);
	}

	public Task<IReadOnlyList<DataTable>> RejectBatchAsync(int batchId, string rejectedBy, string reason, CancellationToken cancellationToken)
	{
		SqlParameter[] parameters =
		{
			CreateIntParameter("@BatchId", batchId),
			CreateTextParameter("@RejectedBy", rejectedBy, 100),
			CreateTextParameter("@Reason", reason, 400)
		};
		return ExecuteProcedureAsync("dbo.usp_RejectBatch", parameters, cancellationToken);
	}

	public Task<IReadOnlyList<DataTable>> PromoteBatchesAsync(int firstBatchId, int lastBatchId, CancellationToken cancellationToken)
	{
		SqlParameter[] parameters =
		{
			CreateIntParameter("@FirstBatchId", firstBatchId),
			CreateIntParameter("@LastBatchId", lastBatchId)
		};
		return ExecuteProcedureAsync("dbo.usp_PromoteBatches", parameters, cancellationToken);
	}

	/// <summary>The lowest and highest Approved batch ids, for "promote everything approved".</summary>
	/// <returns>null when no batch is Approved.</returns>
	public async Task<(int FirstBatchId, int LastBatchId)?> GetApprovedRangeAsync(CancellationToken cancellationToken)
	{
		const string SQL = "SELECT MIN(BatchId), MAX(BatchId) FROM dbo.LoadEvidence_Batch WHERE Status = 'Approved';";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		await reader.ReadAsync(cancellationToken);

		if (reader.IsDBNull(0))
		{
			return null;
		}

		return (reader.GetInt32(0), reader.GetInt32(1));
	}

	public Task<IReadOnlyList<DataTable>> GetCompletenessReportAsync(CancellationToken cancellationToken)
	{
		return ExecuteProcedureAsync("dbo.usp_ReportCompleteness", Array.Empty<SqlParameter>(), cancellationToken);
	}

	public Task<IReadOnlyList<DataTable>> GetSummaryReportAsync(CancellationToken cancellationToken)
	{
		return ExecuteProcedureAsync("dbo.usp_ReportSummary", Array.Empty<SqlParameter>(), cancellationToken);
	}

	public async Task<IReadOnlyList<DataTable>> GetReconciliationAsync(CancellationToken cancellationToken)
	{
		const string SQL = "SELECT * FROM dbo.vw_BatchReconciliation ORDER BY BatchId;";

		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, SQL);
		return await ResultSetReader.ReadAllResultSetsAsync(command, cancellationToken);
	}

	private async Task<IReadOnlyList<DataTable>> ExecuteProcedureAsync(string procedureName, IReadOnlyList<SqlParameter> parameters,
		CancellationToken cancellationToken)
	{
		await using SqlConnection connection = await _connectionFactory.OpenStagingAsync(cancellationToken);
		await using SqlCommand command = SqlConnectionFactory.CreateProcedureCommand(connection, procedureName);
		command.Parameters.AddRange(parameters.ToArray());
		return await ResultSetReader.ReadAllResultSetsAsync(command, cancellationToken);
	}

	private static SqlParameter CreateIntParameter(string name, int value)
	{
		SqlParameter parameter = new SqlParameter(name, SqlDbType.Int);
		parameter.Value = value;
		return parameter;
	}

	private static SqlParameter CreateBitParameter(string name, bool value)
	{
		SqlParameter parameter = new SqlParameter(name, SqlDbType.Bit);
		parameter.Value = value;
		return parameter;
	}

	private static SqlParameter CreateTextParameter(string name, string value, int size)
	{
		SqlParameter parameter = new SqlParameter(name, SqlDbType.NVarChar, size);
		parameter.Value = value;
		return parameter;
	}
}
