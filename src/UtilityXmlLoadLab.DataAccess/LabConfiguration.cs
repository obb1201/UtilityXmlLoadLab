using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace UtilityXmlLoadLab.DataAccess;

/// <summary>
/// Loads configuration for every host: appsettings.json, then user secrets, then environment
/// variables with the prefix "UXL_". The SQL password is never stored in appsettings.json;
/// set it with: dotnet user-secrets set Sql:Password "..." --project src/UtilityXmlLoadLab.Loader
/// </summary>
public static class LabConfiguration
{
	public const string ENV_PREFIX = "UXL_";
	public const string STAGING_CONNECTION_NAME = "SapStaging";
	public const string PASSWORD_KEY = "Sql:Password";

	public static IConfiguration LoadConfiguration(Assembly hostAssembly)
	{
		return new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json", optional: false)
			.AddUserSecrets(hostAssembly, optional: true)
			.AddEnvironmentVariables(ENV_PREFIX)
			.Build();
	}

	/// <summary>Builds the staging connection string from configuration (boundary validation).</summary>
	/// <param name="problem">null on success, otherwise what is missing.</param>
	/// <returns>null when the configuration is incomplete.</returns>
	public static SqlConnectionFactory? CreateConnectionFactory(IConfiguration configuration, out string? problem)
	{
		problem = null;

		string? baseConnectionString = configuration.GetConnectionString(STAGING_CONNECTION_NAME);
		if (string.IsNullOrWhiteSpace(baseConnectionString))
		{
			problem = "ConnectionStrings:" + STAGING_CONNECTION_NAME + " is missing in appsettings.json.";
			return null;
		}

		SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(baseConnectionString);
		string? password = configuration[PASSWORD_KEY];

		if (!builder.IntegratedSecurity && string.IsNullOrEmpty(builder.Password))
		{
			if (string.IsNullOrEmpty(password))
			{
				problem = "The SQL password is not set. Run: dotnet user-secrets set Sql:Password \"<password>\" "
					+ "--project src/UtilityXmlLoadLab.Loader   (or set the environment variable UXL_Sql__Password).";
				return null;
			}

			builder.Password = password;
		}

		return new SqlConnectionFactory(builder.ConnectionString);
	}
}
