using System.Data;

namespace UtilityXmlLoadLab.Business.Benchmarking;

/// <summary>The measurements of one benchmark run (one approach at one degree of parallelism).</summary>
public sealed class BenchmarkRunResult
{
	public string Approach { get; }
	public int DegreeOfParallelism { get; }
	public TimeSpan Elapsed { get; }
	public long MessageCount { get; }
	public long StagedRows { get; }
	public long PassedCount { get; }
	public long FailedCount { get; }
	public int FailedBatchCount { get; }

	/// <summary>Peak CPU of the SQL Server container in % of one core; null when not sampled.</summary>
	public double? PeakSqlCpuPercent { get; }
	public double? AverageSqlCpuPercent { get; }

	/// <summary>CPU time used by this .NET process during the run (the parsing work of approach B).</summary>
	public TimeSpan ClientCpuTime { get; }

	public DataTable Fingerprint { get; }

	public double MessagesPerSecond => MessageCount / Math.Max(Elapsed.TotalSeconds, 0.001);
	public double RowsPerSecond => StagedRows / Math.Max(Elapsed.TotalSeconds, 0.001);

	/// <summary>Linear projection of the elapsed time for 1,000,000 messages.</summary>
	public TimeSpan ProjectedForOneMillion => TimeSpan.FromSeconds(1000000.0 / Math.Max(MessagesPerSecond, 0.001));

	public BenchmarkRunResult(string approach, int degreeOfParallelism, TimeSpan elapsed, long messageCount, long stagedRows,
		long passedCount, long failedCount, int failedBatchCount, double? peakSqlCpuPercent, double? averageSqlCpuPercent,
		TimeSpan clientCpuTime, DataTable fingerprint)
	{
		Approach = approach;
		DegreeOfParallelism = degreeOfParallelism;
		Elapsed = elapsed;
		MessageCount = messageCount;
		StagedRows = stagedRows;
		PassedCount = passedCount;
		FailedCount = failedCount;
		FailedBatchCount = failedBatchCount;
		PeakSqlCpuPercent = peakSqlCpuPercent;
		AverageSqlCpuPercent = averageSqlCpuPercent;
		ClientCpuTime = clientCpuTime;
		Fingerprint = fingerprint;
	}
}
