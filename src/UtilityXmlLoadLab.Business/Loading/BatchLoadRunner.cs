using System.Diagnostics;
using Microsoft.Data.SqlClient;
using UtilityXmlLoadLab.Business.Parsing;
using UtilityXmlLoadLab.Core.Messages;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Business.Loading;

/// <summary>
/// Runs a load over all batches with either approach.
///   A (T-SQL): degree of parallelism 1 runs the SQL Agent-style driver procedure; more than 1
///              runs usp_ShredBatch for several batches at once from separate sessions.
///   B (.NET):  reads each batch, parses it with XmlReader, writes with SqlBulkCopy.
/// One transaction per batch in both. A failing batch is rolled back, logged in LoadRunBatch and
/// skipped; the run goes on (the evidence then shows the batch as not staged).
/// </summary>
public sealed class BatchLoadRunner
{
	private readonly InboxRepository _inboxRepository;
	private readonly LoadRunRepository _loadRunRepository;
	private readonly StagingWriter _stagingWriter;
	private readonly XmlMessageParser _parser = new XmlMessageParser();

	public BatchLoadRunner(InboxRepository inboxRepository, LoadRunRepository loadRunRepository, StagingWriter stagingWriter)
	{
		_inboxRepository = inboxRepository;
		_loadRunRepository = loadRunRepository;
		_stagingWriter = stagingWriter;
	}

	public async Task<LoadRunSummary> RunAsync(EApproach approach, int degreeOfParallelism, bool bOnlyPending, string note,
		CancellationToken cancellationToken)
	{
		Debug.Assert(degreeOfParallelism >= 1);

		if (approach == EApproach.TSql && degreeOfParallelism == 1)
		{
			return await RunTSqlDriverAsync(bOnlyPending, note, cancellationToken);
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		char approachCode = approach == EApproach.TSql ? 'A' : 'B';
		int runId = await _loadRunRepository.StartRunAsync(approachCode, degreeOfParallelism, note, cancellationToken);
		IReadOnlyList<int> batchIds = await _inboxRepository.GetBatchIdsAsync(bOnlyPending, cancellationToken);
		int failedBatchCount = 0;

		ParallelOptions options = new ParallelOptions();
		options.MaxDegreeOfParallelism = degreeOfParallelism;
		options.CancellationToken = cancellationToken;

		await Parallel.ForEachAsync(batchIds, options, async (batchId, token) =>
		{
			DateTime startedAt = DateTime.UtcNow;
			string? errorMessage = null;

			try
			{
				if (approach == EApproach.TSql)
				{
					await _loadRunRepository.ShredBatchAsync(batchId, token);
				}
				else
				{
					await LoadBatchWithDotNetAsync(batchId, token);
				}
			}
			catch (SqlException exception)
			{
				// Boundary of one batch: the transaction was rolled back; log it and continue.
				errorMessage = "Error " + exception.Number + ": " + exception.Message;
				Interlocked.Increment(ref failedBatchCount);
			}

			await _loadRunRepository.LogBatchAsync(runId, batchId, startedAt, DateTime.UtcNow, errorMessage, token);
		});

		await _loadRunRepository.EndRunAsync(runId, batchIds.Count, failedBatchCount, cancellationToken);
		stopwatch.Stop();
		return new LoadRunSummary(runId, batchIds.Count, failedBatchCount, stopwatch.Elapsed);
	}

	private async Task LoadBatchWithDotNetAsync(int batchId, CancellationToken cancellationToken)
	{
		IReadOnlyList<RawMessage> messages = await _inboxRepository.GetBatchMessagesAsync(batchId, cancellationToken);
		List<MessageResult> results = messages.Select(message => _parser.ParseMessage(message)).ToList();
		await _stagingWriter.WriteBatchAsync(batchId, results, cancellationToken);
	}

	private async Task<LoadRunSummary> RunTSqlDriverAsync(bool bOnlyPending, string note, CancellationToken cancellationToken)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		System.Data.DataTable summary = await _loadRunRepository.RunShredDriverAsync(bOnlyPending, note, cancellationToken);
		stopwatch.Stop();

		System.Data.DataRow row = summary.Rows[0];
		return new LoadRunSummary(
			Convert.ToInt32(row["RunId"]),
			Convert.ToInt32(row["BatchCount"]),
			Convert.ToInt32(row["FailedBatchCount"]),
			stopwatch.Elapsed);
	}
}
