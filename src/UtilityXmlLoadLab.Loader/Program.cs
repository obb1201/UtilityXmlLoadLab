using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using UtilityXmlLoadLab.Business.Hosting;
using UtilityXmlLoadLab.Business.Loading;
using UtilityXmlLoadLab.Core.Messages;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Loader;

/// <summary>
/// Operator console: deploy the database, create batches, run approach A or B, check references,
/// approve / reject / promote batches, and read the evidence reports.
/// </summary>
public static class Program
{
	private const string USAGE = @"Usage: UtilityXmlLoadLab.Loader <command> [options]

Commands
  deploy-db [--sql-dir <path>]           run the numbered scripts in sql/ (idempotent)
  create-batches [--batch-size <n>]      put unbatched Inbox messages into batches (default 1000)
  load --approach A|B [--dop <n>] [--reload]
                                         A = T-SQL usp_ShredBatch (dop 1 = driver procedure),
                                         B = .NET XmlReader + SqlBulkCopy. --reload also reloads
                                         batches that are Staged (not Approved/Promoted).
  check                                  run the cross-entity reference checks (usp_CheckReferences)
  approve --batch <n> --by <name>        approve one batch manually
  approve --auto [--by <name>]           approve every perfectly clean batch
  reject --batch <n> --by <name> --reason <text>
  promote --batch <n> | --from <n> --to <m> | --all
                                         MERGE approved batches into SapTarget (in batch order)
  report [summary|completeness|reconciliation|all]
  reset-staging [--with-target]          empty staging and evidence (Inbox and batches stay)";

	public static async Task<int> Main(string[] args)
	{
		CommandLineArgs options = CommandLineArgs.Parse(args);
		if (options.Positionals.Count == 0 || options.HasFlag("help"))
		{
			Console.WriteLine(USAGE);
			return options.Positionals.Count == 0 && !options.HasFlag("help") ? 2 : 0;
		}

		IConfiguration configuration = LabConfiguration.LoadConfiguration(typeof(Program).Assembly);
		SqlConnectionFactory? connectionFactory = LabConfiguration.CreateConnectionFactory(configuration, out string? problem);
		if (connectionFactory == null)
		{
			Console.Error.WriteLine("Error: " + problem);
			return 2;
		}

		try
		{
			return await RunCommandAsync(options.Positionals[0].ToLowerInvariant(), options, configuration, connectionFactory);
		}
		catch (SqlException exception)
		{
			// Business rule refusals from the procedures arrive here (THROW 500xx) with a clear message.
			Console.Error.WriteLine("SQL error " + exception.Number + ": " + exception.Message);
			return 1;
		}
		catch (IOException exception)
		{
			Console.Error.WriteLine("File error: " + exception.Message);
			return 1;
		}
	}

	private static async Task<int> RunCommandAsync(string command, CommandLineArgs options, IConfiguration configuration,
		SqlConnectionFactory connectionFactory)
	{
		CancellationToken cancellationToken = CancellationToken.None;
		GateRepository gate = new GateRepository(connectionFactory);

		switch (command)
		{
			case "deploy-db":
				return await DeployDatabaseAsync(options, connectionFactory, cancellationToken);

			case "create-batches":
			{
				int batchSize = options.GetInt("batch-size", configuration.GetValue("Lab:BatchSize", 1000), 1);
				if (!ReportProblems(options))
				{
					return 2;
				}

				int batchCount = await new InboxRepository(connectionFactory).CreateBatchesAsync(batchSize, cancellationToken);
				Console.WriteLine($"Created {batchCount:N0} batches of up to {batchSize:N0} messages.");
				return 0;
			}

			case "load":
				return await LoadAsync(options, connectionFactory, cancellationToken);

			case "check":
				Console.WriteLine("Checking references across all Staged and Approved batches ...");
				PrintTables(await gate.CheckReferencesAsync(cancellationToken));
				return 0;

			case "approve":
				return await ApproveAsync(options, gate, cancellationToken);

			case "reject":
			{
				int batchId = options.GetInt("batch", 0, 1);
				string? rejectedBy = options.GetTextOrNull("by");
				string? reason = options.GetTextOrNull("reason");
				if (batchId == 0 || string.IsNullOrWhiteSpace(rejectedBy) || string.IsNullOrWhiteSpace(reason))
				{
					options.Problems.Add("reject needs --batch <n> --by <name> --reason <text>.");
				}

				if (!ReportProblems(options))
				{
					return 2;
				}

				await gate.RejectBatchAsync(batchId, rejectedBy!, reason!, cancellationToken);
				Console.WriteLine($"Batch {batchId} rejected.");
				return 0;
			}

			case "promote":
				return await PromoteAsync(options, gate, cancellationToken);

			case "report":
				return await ReportAsync(options, gate, cancellationToken);

			case "reset-staging":
				await new BenchmarkRepository(connectionFactory).ResetStagingAsync(options.HasFlag("with-target"), cancellationToken);
				Console.WriteLine(options.HasFlag("with-target") ? "Staging, evidence and target emptied." : "Staging and evidence emptied.");
				return 0;

			default:
				Console.Error.WriteLine("Unknown command: " + command);
				Console.Error.WriteLine(USAGE);
				return 2;
		}
	}

	private static async Task<int> DeployDatabaseAsync(CommandLineArgs options, SqlConnectionFactory connectionFactory, CancellationToken cancellationToken)
	{
		string? sqlDirectory = options.GetTextOrNull("sql-dir") ?? ScriptDeployer.FindSqlDirectory(AppContext.BaseDirectory)
			?? ScriptDeployer.FindSqlDirectory(Directory.GetCurrentDirectory());

		if (sqlDirectory == null || !Directory.Exists(sqlDirectory))
		{
			Console.Error.WriteLine("Error: sql/ folder not found. Pass --sql-dir <path>.");
			return 2;
		}

		Console.WriteLine("Deploying scripts from " + sqlDirectory + " ...");
		IReadOnlyList<string> scripts = await new ScriptDeployer(connectionFactory).DeployAsync(sqlDirectory, cancellationToken);
		scripts.ToList().ForEach(script => Console.WriteLine("  " + script));
		Console.WriteLine("Done.");
		return 0;
	}

	private static async Task<int> LoadAsync(CommandLineArgs options, SqlConnectionFactory connectionFactory, CancellationToken cancellationToken)
	{
		string approachText = options.GetText("approach", string.Empty).ToUpperInvariant();
		int degreeOfParallelism = options.GetInt("dop", approachText == "B" ? Environment.ProcessorCount / 2 : 1, 1);

		if (approachText != "A" && approachText != "B")
		{
			options.Problems.Add("load needs --approach A or --approach B.");
		}

		if (!ReportProblems(options))
		{
			return 2;
		}

		EApproach approach = approachText == "A" ? EApproach.TSql : EApproach.DotNet;
		InboxRepository inboxRepository = new InboxRepository(connectionFactory);
		LoadRunRepository loadRunRepository = new LoadRunRepository(connectionFactory);
		BatchLoadRunner runner = new BatchLoadRunner(inboxRepository, loadRunRepository, new StagingWriter(connectionFactory));

		Console.WriteLine($"Loading with approach {approachText}, degree of parallelism {degreeOfParallelism} ...");
		LoadRunSummary summary = await runner.RunAsync(approach, degreeOfParallelism, !options.HasFlag("reload"), "Loader CLI", cancellationToken);

		(long passed, long failed, long duplicates, long stagedRows, int stagedBatches) =
			await new BenchmarkRepository(connectionFactory).GetStagedTotalsAsync(cancellationToken);

		Console.WriteLine($"Run {summary.RunId}: {summary.BatchCount:N0} batches ({summary.FailedBatchCount} failed) in {summary.Elapsed.TotalSeconds:N1} s.");
		Console.WriteLine($"Staged batches: {stagedBatches:N0}; messages passed {passed:N0}, failed {failed:N0}, duplicate arrivals {duplicates:N0}; staged rows {stagedRows:N0}.");

		if (summary.FailedBatchCount > 0)
		{
			PrintTables(new[] { await loadRunRepository.GetFailedBatchesAsync(summary.RunId, cancellationToken) });
			return 1;
		}

		return 0;
	}

	private static async Task<int> ApproveAsync(CommandLineArgs options, GateRepository gate, CancellationToken cancellationToken)
	{
		if (options.HasFlag("auto"))
		{
			IReadOnlyList<DataTable> approved = await gate.AutoApproveBatchesAsync(options.GetText("by", "auto-approve"), cancellationToken);
			int count = approved.Count == 0 ? 0 : approved[0].Rows.Count;
			Console.WriteLine($"Auto-approved {count:N0} clean batches (FailedCount = 0 and no findings).");
			return 0;
		}

		int batchId = options.GetInt("batch", 0, 1);
		string? approvedBy = options.GetTextOrNull("by");
		if (batchId == 0 || string.IsNullOrWhiteSpace(approvedBy))
		{
			options.Problems.Add("approve needs --batch <n> --by <name>, or --auto.");
		}

		if (!ReportProblems(options))
		{
			return 2;
		}

		await gate.ApproveBatchAsync(batchId, approvedBy!, cancellationToken);
		Console.WriteLine($"Batch {batchId} approved by {approvedBy}.");
		return 0;
	}

	private static async Task<int> PromoteAsync(CommandLineArgs options, GateRepository gate, CancellationToken cancellationToken)
	{
		int firstBatchId;
		int lastBatchId;

		if (options.HasFlag("all"))
		{
			(int FirstBatchId, int LastBatchId)? range = await gate.GetApprovedRangeAsync(cancellationToken);
			if (range == null)
			{
				Console.WriteLine("No Approved batches to promote.");
				return 0;
			}

			firstBatchId = range.Value.FirstBatchId;
			lastBatchId = range.Value.LastBatchId;
		}
		else if (options.HasFlag("batch"))
		{
			firstBatchId = options.GetInt("batch", 0, 1);
			lastBatchId = firstBatchId;
		}
		else
		{
			firstBatchId = options.GetInt("from", 0, 1);
			lastBatchId = options.GetInt("to", 0, 1);
			if (firstBatchId == 0 || lastBatchId < firstBatchId)
			{
				options.Problems.Add("promote needs --batch <n>, --from <n> --to <m>, or --all.");
			}
		}

		if (!ReportProblems(options))
		{
			return 2;
		}

		Console.WriteLine($"Promoting batches {firstBatchId} to {lastBatchId} into SapTarget ...");
		PrintTables(await gate.PromoteBatchesAsync(firstBatchId, lastBatchId, cancellationToken));
		return 0;
	}

	private static async Task<int> ReportAsync(CommandLineArgs options, GateRepository gate, CancellationToken cancellationToken)
	{
		string which = options.Positionals.Count > 1 ? options.Positionals[1].ToLowerInvariant() : "all";

		if (which == "summary" || which == "all")
		{
			Console.WriteLine("== Summary");
			PrintTables(await gate.GetSummaryReportAsync(cancellationToken));
		}

		if (which == "completeness" || which == "all")
		{
			Console.WriteLine("== Completeness (every line should read OK)");
			PrintTables(await gate.GetCompletenessReportAsync(cancellationToken));
		}

		if (which == "reconciliation" || which == "all")
		{
			Console.WriteLine("== Reconciliation per batch (staged vs promoted)");
			PrintTables(await gate.GetReconciliationAsync(cancellationToken));
		}

		return 0;
	}

	private static void PrintTables(IEnumerable<DataTable> tables)
	{
		foreach (DataTable table in tables.Where(table => table.Columns.Count > 0))
		{
			Console.WriteLine(TablePrinter.FormatTable(table));
		}
	}

	private static bool ReportProblems(CommandLineArgs options)
	{
		options.Problems.ForEach(problem => Console.Error.WriteLine("Error: " + problem));
		return options.Problems.Count == 0;
	}
}
