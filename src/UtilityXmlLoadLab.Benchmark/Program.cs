using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using UtilityXmlLoadLab.Business.Benchmarking;
using UtilityXmlLoadLab.Business.Hosting;
using UtilityXmlLoadLab.Business.Loading;
using UtilityXmlLoadLab.Core.Messages;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Benchmark;

/// <summary>
/// Benchmark harness: resets staging, runs approach A and B over the same Inbox data at the given
/// degrees of parallelism, checks that all runs wrote identical output, and prints a comparison
/// with a projection for 1M messages. Generate data first with the Generator.
/// </summary>
public static class Program
{
	private const string USAGE = @"Usage: UtilityXmlLoadLab.Benchmark [options]
  --approaches A,B      approaches to run (default A,B)
  --dops 1,4            degrees of parallelism to run for each approach (default 1,4)
  --no-cpu              do not sample SQL Server container CPU with docker stats
  --container <name>    SQL Server container name (default from appsettings: Benchmark:SqlContainerName)
  --output <folder>     where to write the markdown report (default benchmark-results)";

	public static async Task<int> Main(string[] args)
	{
		CommandLineArgs options = CommandLineArgs.Parse(args);
		if (options.HasFlag("help"))
		{
			Console.WriteLine(USAGE);
			return 0;
		}

		IConfiguration configuration = LabConfiguration.LoadConfiguration(typeof(Program).Assembly);
		IReadOnlyList<int> dops = options.GetIntList("dops", new[] { 1, 4 });
		string containerName = options.GetText("container", configuration["Benchmark:SqlContainerName"] ?? "uxl-sql");
		string outputFolder = options.GetText("output", "benchmark-results");
		List<EApproach> approaches = new List<EApproach>();

		foreach (string part in options.GetText("approaches", "A,B").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			switch (part.ToUpperInvariant())
			{
				case "A":
					approaches.Add(EApproach.TSql);
					break;
				case "B":
					approaches.Add(EApproach.DotNet);
					break;
				default:
					options.Problems.Add("--approaches may only contain A and B.");
					break;
			}
		}

		SqlConnectionFactory? connectionFactory = LabConfiguration.CreateConnectionFactory(configuration, out string? problem);
		if (problem != null)
		{
			options.Problems.Add(problem);
		}

		if (options.Problems.Count > 0 || connectionFactory == null || approaches.Count == 0)
		{
			options.Problems.ForEach(item => Console.Error.WriteLine("Error: " + item));
			Console.Error.WriteLine(USAGE);
			return 2;
		}

		try
		{
			return await RunAsync(connectionFactory, approaches, dops, options.HasFlag("no-cpu") ? null : new DockerCpuSampler(containerName), outputFolder);
		}
		catch (SqlException exception)
		{
			Console.Error.WriteLine("SQL error " + exception.Number + ": " + exception.Message);
			return 1;
		}
		catch (IOException exception)
		{
			Console.Error.WriteLine("Could not write the report: " + exception.Message);
			return 1;
		}
	}

	private static async Task<int> RunAsync(SqlConnectionFactory connectionFactory, IReadOnlyList<EApproach> approaches, IReadOnlyList<int> dops,
		DockerCpuSampler? cpuSamplerOrNull, string outputFolder)
	{
		CancellationToken cancellationToken = CancellationToken.None;
		InboxRepository inboxRepository = new InboxRepository(connectionFactory);
		BenchmarkRepository benchmarkRepository = new BenchmarkRepository(connectionFactory);

		(long messageCount, long duplicateCount, int batchCount) = await inboxRepository.GetInboxCountsAsync(cancellationToken);
		if (batchCount == 0)
		{
			Console.Error.WriteLine("Error: the Inbox has no batches. Run the Generator first (it creates batches).");
			return 2;
		}

		Console.WriteLine($"Inbox: {messageCount:N0} messages, {duplicateCount:N0} duplicate arrivals, {batchCount:N0} batches.");

		BatchLoadRunner loadRunner = new BatchLoadRunner(inboxRepository, new LoadRunRepository(connectionFactory), new StagingWriter(connectionFactory));
		BenchmarkRunner runner = new BenchmarkRunner(inboxRepository, benchmarkRepository, loadRunner);
		IReadOnlyList<BenchmarkRunResult> results = await runner.RunAsync(approaches, dops, cpuSamplerOrNull, Console.WriteLine, cancellationToken);

		IReadOnlyList<string> differences = BenchmarkRunner.CompareFingerprints(results);
		string serverDescription = await benchmarkRepository.GetServerDescriptionAsync(cancellationToken);
		string markdown = BenchmarkRunner.FormatMarkdown(results, differences, serverDescription, cpuSamplerOrNull?.ProblemOrNull);

		Directory.CreateDirectory(outputFolder);
		string reportPath = Path.Combine(outputFolder, "benchmark-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md");
		await File.WriteAllTextAsync(reportPath, markdown, cancellationToken);

		Console.WriteLine();
		Console.WriteLine(markdown);
		Console.WriteLine("Report written to " + Path.GetFullPath(reportPath));
		Console.WriteLine("Staging now holds the output of the last run; continue with: Loader check / approve / promote.");

		bool bHasFailures = differences.Count > 0 || results.Any(result => result.FailedBatchCount > 0);
		return bHasFailures ? 1 : 0;
	}
}
