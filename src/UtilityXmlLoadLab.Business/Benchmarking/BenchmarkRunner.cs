using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UtilityXmlLoadLab.Business.Loading;
using UtilityXmlLoadLab.Core.Messages;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Business.Benchmarking;

/// <summary>
/// Runs approach A and B over the same Inbox data. Before each run, staging and evidence are reset
/// (the Inbox and its batches stay), so every run starts from the same state. After each run it
/// fingerprints the staged output; identical fingerprints prove both approaches wrote the same data.
/// </summary>
public sealed class BenchmarkRunner
{
	private readonly InboxRepository _inboxRepository;
	private readonly BenchmarkRepository _benchmarkRepository;
	private readonly BatchLoadRunner _loadRunner;

	public BenchmarkRunner(InboxRepository inboxRepository, BenchmarkRepository benchmarkRepository, BatchLoadRunner loadRunner)
	{
		_inboxRepository = inboxRepository;
		_benchmarkRepository = benchmarkRepository;
		_loadRunner = loadRunner;
	}

	public async Task<IReadOnlyList<BenchmarkRunResult>> RunAsync(IReadOnlyList<EApproach> approaches, IReadOnlyList<int> degreesOfParallelism,
		DockerCpuSampler? cpuSamplerOrNull, Action<string> log, CancellationToken cancellationToken)
	{
		(long messageCount, long _, int batchCount) = await _inboxRepository.GetInboxCountsAsync(cancellationToken);
		List<BenchmarkRunResult> results = new List<BenchmarkRunResult>();

		foreach (EApproach approach in approaches)
		{
			foreach (int degreeOfParallelism in degreesOfParallelism)
			{
				string approachCode = approach == EApproach.TSql ? "A" : "B";
				log($"Run {approachCode} (T-SQL={approach == EApproach.TSql}), dop {degreeOfParallelism}: resetting staging ...");
				await _benchmarkRepository.ResetStagingAsync(false, cancellationToken);

				TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
				cpuSamplerOrNull?.Start();

				LoadRunSummary summary = await _loadRunner.RunAsync(approach, degreeOfParallelism, true,
					"Benchmark " + approachCode + " dop " + degreeOfParallelism.ToString(CultureInfo.InvariantCulture), cancellationToken);

				if (cpuSamplerOrNull != null)
				{
					await cpuSamplerOrNull.StopAsync();
				}

				TimeSpan clientCpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
				(long passed, long failed, long _, long stagedRows, int _) = await _benchmarkRepository.GetStagedTotalsAsync(cancellationToken);
				DataTable fingerprint = await _benchmarkRepository.GetFingerprintAsync(cancellationToken);

				double? peak = cpuSamplerOrNull != null && cpuSamplerOrNull.Samples.Count > 0 ? cpuSamplerOrNull.Samples.Max() : null;
				double? average = cpuSamplerOrNull != null && cpuSamplerOrNull.Samples.Count > 0 ? cpuSamplerOrNull.Samples.Average() : null;

				BenchmarkRunResult result = new BenchmarkRunResult(approachCode, degreeOfParallelism, summary.Elapsed, messageCount, stagedRows,
					passed, failed, summary.FailedBatchCount, peak, average, clientCpu, fingerprint);
				results.Add(result);

				log($"  {summary.BatchCount}/{batchCount} batches, {summary.Elapsed.TotalSeconds:N2} s, {result.MessagesPerSecond:N0} msgs/s, "
					+ $"{result.RowsPerSecond:N0} rows/s, failed batches {summary.FailedBatchCount}");
			}
		}

		return results;
	}

	/// <returns>An empty list when all runs wrote identical output, otherwise one line per difference.</returns>
	public static IReadOnlyList<string> CompareFingerprints(IReadOnlyList<BenchmarkRunResult> results)
	{
		List<string> differences = new List<string>();
		if (results.Count < 2)
		{
			return differences;
		}

		BenchmarkRunResult baseline = results[0];
		foreach (BenchmarkRunResult other in results.Skip(1))
		{
			// Compare by table name, never by row position.
			Dictionary<string, DataRow> actualByTable = other.Fingerprint.Rows.Cast<DataRow>()
				.ToDictionary(row => (string)row["TableName"], StringComparer.Ordinal);

			foreach (DataRow expected in baseline.Fingerprint.Rows)
			{
				bool bIsSame = actualByTable.TryGetValue((string)expected["TableName"], out DataRow? actual)
					&& Equals(expected["RowCnt"], actual["RowCnt"])
					&& Equals(expected["ContentChecksum"], actual["ContentChecksum"]);

				if (!bIsSame)
				{
					differences.Add($"{other.Approach} dop {other.DegreeOfParallelism} vs {baseline.Approach} dop {baseline.DegreeOfParallelism}: "
						+ $"{expected["TableName"]} rows {actual?["RowCnt"]} vs {expected["RowCnt"]}, content differs");
				}
			}
		}

		return differences;
	}

	public static string FormatMarkdown(IReadOnlyList<BenchmarkRunResult> results, IReadOnlyList<string> differences, string serverDescription,
		string? cpuProblemOrNull)
	{
		StringBuilder text = new StringBuilder();
		BenchmarkRunResult first = results[0];

		text.AppendLine("# Benchmark results");
		text.AppendLine();
		text.AppendLine($"- Date (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
		text.AppendLine($"- Server: {serverDescription}");
		text.AppendLine($"- Client: {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}");
		text.AppendLine($"- Data: {first.MessageCount:N0} messages ({first.PassedCount:N0} passed, {first.FailedCount:N0} failed incl. conflicting duplicates), {first.StagedRows:N0} staged rows");
		text.AppendLine("- Each run starts from empty staging; one transaction per batch of 1,000 messages.");
		text.AppendLine();
		text.AppendLine("| Approach | DOP | Elapsed (s) | Msgs/s | Rows/s | Peak SQL CPU | Avg SQL CPU | Client CPU (s) | Failed batches | Projected 1M |");
		text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

		foreach (BenchmarkRunResult result in results)
		{
			string name = result.Approach == "A" ? "A: T-SQL shredding" : "B: .NET XmlReader + SqlBulkCopy";
			text.AppendLine($"| {name} | {result.DegreeOfParallelism} | {result.Elapsed.TotalSeconds:N2} | {result.MessagesPerSecond:N0} | "
				+ $"{result.RowsPerSecond:N0} | {FormatCpu(result.PeakSqlCpuPercent)} | {FormatCpu(result.AverageSqlCpuPercent)} | "
				+ $"{result.ClientCpuTime.TotalSeconds:N1} | {result.FailedBatchCount} | {FormatDuration(result.ProjectedForOneMillion)} |");
		}

		text.AppendLine();
		text.AppendLine("CPU is in % of one core (the SQL container can go above 100%). Client CPU is the CPU time of the benchmark process (the parsing work of approach B).");
		if (cpuProblemOrNull != null)
		{
			text.AppendLine();
			text.AppendLine("CPU sampling: " + cpuProblemOrNull);
		}

		text.AppendLine();
		text.AppendLine("## Output comparison");
		text.AppendLine();
		if (differences.Count == 0)
		{
			text.AppendLine("All runs wrote identical staging and evidence data (row counts and content checksums match for every table).");
		}
		else
		{
			text.AppendLine("DIFFERENCES FOUND:");
			differences.ToList().ForEach(difference => text.AppendLine("- " + difference));
		}

		text.AppendLine();
		text.AppendLine("| Table | Rows | Content checksum |");
		text.AppendLine("|---|---:|---:|");
		foreach (DataRow row in first.Fingerprint.Rows)
		{
			text.AppendLine($"| {row["TableName"]} | {Convert.ToInt64(row["RowCnt"]):N0} | {row["ContentChecksum"]} |");
		}

		return text.ToString();
	}

	private static string FormatCpu(double? percentOrNull)
	{
		return percentOrNull.HasValue ? percentOrNull.Value.ToString("N0", CultureInfo.InvariantCulture) + "%" : "n/a";
	}

	private static string FormatDuration(TimeSpan duration)
	{
		return duration.TotalHours >= 1
			? $"{(int)duration.TotalHours} h {duration.Minutes:D2} min"
			: $"{duration.Minutes} min {duration.Seconds:D2} s";
	}
}
