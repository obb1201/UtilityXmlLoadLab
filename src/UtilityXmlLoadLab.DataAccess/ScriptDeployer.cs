using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>
/// Runs the numbered scripts in the sql/ folder in name order. Each script is idempotent, so
/// deploying again is safe. Batches are split on lines that contain only "GO".
/// </summary>
public sealed class ScriptDeployer
{
	private static readonly Regex GO_SEPARATOR = new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly SqlConnectionFactory _connectionFactory;

	public ScriptDeployer(SqlConnectionFactory connectionFactory)
	{
		_connectionFactory = connectionFactory;
	}

	/// <returns>The script file names that were run, in order.</returns>
	public async Task<IReadOnlyList<string>> DeployAsync(string sqlDirectory, CancellationToken cancellationToken)
	{
		string[] scriptPaths = Directory.GetFiles(sqlDirectory, "*.sql");
		Array.Sort(scriptPaths, StringComparer.OrdinalIgnoreCase);

		await using SqlConnection connection = await _connectionFactory.OpenMasterAsync(cancellationToken);
		List<string> deployed = new List<string>();

		foreach (string scriptPath in scriptPaths)
		{
			string script = await File.ReadAllTextAsync(scriptPath, cancellationToken);

			foreach (string batch in GO_SEPARATOR.Split(script).Where(batch => batch.Trim().Length > 0))
			{
				await using SqlCommand command = SqlConnectionFactory.CreateTextCommand(connection, batch);
				await command.ExecuteNonQueryAsync(cancellationToken);
			}

			deployed.Add(Path.GetFileName(scriptPath));
		}

		return deployed;
	}

	/// <summary>Finds the sql/ folder by walking up from <paramref name="startDirectory"/>.</summary>
	/// <returns>null when no sql/ folder with numbered scripts is found.</returns>
	public static string? FindSqlDirectory(string startDirectory)
	{
		DirectoryInfo? directory = new DirectoryInfo(startDirectory);

		while (directory != null)
		{
			string candidate = Path.Combine(directory.FullName, "sql");
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "001_CreateDatabases.sql")))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		return null;
	}
}
