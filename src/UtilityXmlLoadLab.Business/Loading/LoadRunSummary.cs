namespace UtilityXmlLoadLab.Business.Loading;

/// <summary>The result of one load run (all batches, one approach, one degree of parallelism).</summary>
public sealed class LoadRunSummary
{
	public int RunId { get; }
	public int BatchCount { get; }
	public int FailedBatchCount { get; }
	public TimeSpan Elapsed { get; }

	public LoadRunSummary(int runId, int batchCount, int failedBatchCount, TimeSpan elapsed)
	{
		RunId = runId;
		BatchCount = batchCount;
		FailedBatchCount = failedBatchCount;
		Elapsed = elapsed;
	}
}
