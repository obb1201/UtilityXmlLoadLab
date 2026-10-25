using Microsoft.Data.SqlClient;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>Opens connections to SapStaging (and to master, for deploying the scripts).</summary>
public sealed class SqlConnectionFactory
{
	/// <summary>Long-running statements (shredding, checks, promotion) get 30 minutes.</summary>
	public const int LONG_COMMAND_TIMEOUT_SECONDS = 1800;

	public string StagingConnectionString { get; }

	public SqlConnectionFactory(string stagingConnectionString)
	{
		StagingConnectionString = stagingConnectionString;
	}

	public async Task<SqlConnection> OpenStagingAsync(CancellationToken cancellationToken)
	{
		SqlConnection connection = new SqlConnection(StagingConnectionString);
		await connection.OpenAsync(cancellationToken);
		return connection;
	}

	public async Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken)
	{
		SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(StagingConnectionString);
		builder.InitialCatalog = "master";
		SqlConnection connection = new SqlConnection(builder.ConnectionString);
		await connection.OpenAsync(cancellationToken);
		return connection;
	}

	public static SqlCommand CreateProcedureCommand(SqlConnection connection, string procedureName)
	{
		SqlCommand command = connection.CreateCommand();
		command.CommandType = System.Data.CommandType.StoredProcedure;
		command.CommandText = procedureName;
		command.CommandTimeout = LONG_COMMAND_TIMEOUT_SECONDS;
		return command;
	}

	public static SqlCommand CreateTextCommand(SqlConnection connection, string sql)
	{
		SqlCommand command = connection.CreateCommand();
		command.CommandType = System.Data.CommandType.Text;
		command.CommandText = sql;
		command.CommandTimeout = LONG_COMMAND_TIMEOUT_SECONDS;
		return command;
	}
}
