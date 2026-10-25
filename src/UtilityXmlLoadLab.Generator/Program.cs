using System.Diagnostics;
using System.ServiceModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using UtilityXmlLoadLab.Business.Delivery;
using UtilityXmlLoadLab.Business.Generation;
using UtilityXmlLoadLab.Business.Hosting;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Generator;

/// <summary>
/// Mock SAP message generator. Creates synthetic IS-U style XML messages and delivers them to the
/// Inbox, either through the CoreWCF receiver or straight into the table (fast tests).
/// </summary>
public static class Program
{
	private const string USAGE = @"Usage: UtilityXmlLoadLab.Generator [options]
  --count <n>                 messages to generate (default 10000, up to 1000000)
  --seed <n>                  random seed, same seed = same data (default 42)
  --fault-rate <r>            share for EACH fault: malformed, missing required, bad value, duplicate (default 0.01)
  --business-fault-rate <r>   share for EACH business fault: overlapping contract, meter installed twice (default 0.002)
  --target inbox|wcf          deliver straight into dbo.Inbox (default) or through the CoreWCF endpoint
  --url <url>                 CoreWCF endpoint (default from appsettings: Generator:ServiceUrl)
  --send-parallel <n>         parallel calls when --target wcf (default 8)
  --batch-size <n>            create batches of this size after delivery (default 1000)
  --no-batches                do not create batches
  --reset                     empty Inbox, staging, evidence and target first";

	public static async Task<int> Main(string[] args)
	{
		CommandLineArgs options = CommandLineArgs.Parse(args);
		if (options.HasFlag("help"))
		{
			Console.WriteLine(USAGE);
			return 0;
		}

		IConfiguration configuration = LabConfiguration.LoadConfiguration(typeof(Program).Assembly);
		int messageCount = options.GetInt("count", 10000, 1);
		int seed = options.GetInt("seed", 42, 0);
		double faultRate = options.GetRate("fault-rate", 0.01);
		double businessFaultRate = options.GetRate("business-fault-rate", 0.002);
		string target = options.GetText("target", "inbox").ToLowerInvariant();
		string url = options.GetText("url", configuration["Generator:ServiceUrl"] ?? "http://localhost:5080/InboxService.svc");
		int sendParallel = options.GetInt("send-parallel", 8, 1);
		int batchSize = options.GetInt("batch-size", configuration.GetValue("Lab:BatchSize", 1000), 1);

		if (messageCount > 1000000)
		{
			options.Problems.Add("--count may be at most 1000000.");
		}

		if (target != "inbox" && target != "wcf")
		{
			options.Problems.Add("--target must be inbox or wcf.");
		}

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint))
		{
			options.Problems.Add("--url is not a valid absolute URL.");
		}

		SqlConnectionFactory? connectionFactory = LabConfiguration.CreateConnectionFactory(configuration, out string? configProblem);
		if (configProblem != null)
		{
			options.Problems.Add(configProblem);
		}

		if (options.Problems.Count > 0 || connectionFactory == null || endpoint == null)
		{
			options.Problems.ForEach(problem => Console.Error.WriteLine("Error: " + problem));
			Console.Error.WriteLine(USAGE);
			return 2;
		}

		try
		{
			return await RunAsync(connectionFactory, options, messageCount, seed, faultRate, businessFaultRate, target, endpoint,
				sendParallel, batchSize);
		}
		catch (SqlException exception)
		{
			Console.Error.WriteLine("SQL error " + exception.Number + ": " + exception.Message);
			return 1;
		}
		catch (CommunicationException exception)
		{
			Console.Error.WriteLine("Could not reach the CoreWCF receiver at " + endpoint + ": " + exception.Message);
			return 1;
		}
		catch (TimeoutException exception)
		{
			Console.Error.WriteLine("The CoreWCF receiver timed out: " + exception.Message);
			return 1;
		}
	}

	private static async Task<int> RunAsync(SqlConnectionFactory connectionFactory, CommandLineArgs options, int messageCount, int seed,
		double faultRate, double businessFaultRate, string target, Uri endpoint, int sendParallel, int batchSize)
	{
		CancellationToken cancellationToken = CancellationToken.None;
		InboxRepository inboxRepository = new InboxRepository(connectionFactory);

		if (options.HasFlag("reset"))
		{
			Console.WriteLine("Resetting Inbox, staging, evidence and target ...");
			await new BenchmarkRepository(connectionFactory).ResetAllAsync(cancellationToken);
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		Console.WriteLine($"Generating {messageCount:N0} messages (seed {seed}, fault rate {faultRate}, business fault rate {businessFaultRate}) ...");

		MessageGenerator generator = new MessageGenerator(seed);
		IReadOnlyList<GeneratedMessage> messages = generator.GenerateMessages(new GenerationOptions(messageCount, seed, faultRate, businessFaultRate));
		List<GeneratedMessage> arrivals = new FaultInjector(seed + 1).InjectFaults(messages, faultRate);
		Console.WriteLine($"  generated {messages.Count:N0} messages + {arrivals.Count - messages.Count:N0} duplicate arrivals, "
			+ $"{messages.Sum(message => message.ExpectedRowCount):N0} segments, in {stopwatch.Elapsed.TotalSeconds:N1} s");

		foreach (IGrouping<string, GeneratedMessage> group in messages.GroupBy(message => message.MessageType).OrderBy(group => group.Key))
		{
			Console.WriteLine($"    {group.Key,-16} {group.Count(),8:N0}");
		}

		foreach (IGrouping<EFaultKind, GeneratedMessage> group in messages.Where(message => message.FaultKind != EFaultKind.None)
			.GroupBy(message => message.FaultKind).OrderBy(group => group.Key))
		{
			Console.WriteLine($"    fault {group.Key,-18} {group.Count(),5:N0}");
		}

		stopwatch.Restart();
		DeliveryResult result;
		if (target == "wcf")
		{
			Console.WriteLine($"Sending to {endpoint} with {sendParallel} parallel calls ...");
			result = await MessageSender.SendToServiceAsync(arrivals, endpoint, sendParallel, cancellationToken);
		}
		else
		{
			Console.WriteLine("Writing straight into dbo.Inbox ...");
			result = await new MessageSender(inboxRepository).SendDirectAsync(arrivals, cancellationToken);
		}

		double messagesPerSecond = arrivals.Count / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
		Console.WriteLine($"  accepted {result.AcceptedCount:N0}, duplicates {result.DuplicateCount:N0}, rejected {result.RejectedCount:N0} "
			+ $"in {stopwatch.Elapsed.TotalSeconds:N1} s ({messagesPerSecond:N0} msgs/s)");

		foreach (string problem in result.Problems.Take(20))
		{
			Console.WriteLine("  rejected: " + problem);
		}

		List<ManifestEntry> manifest = arrivals
			.Select(message => new ManifestEntry(message.MessageId, message.ArrivalNo, message.MessageType, message.FaultKind.ToString(), message.ExpectedRowCount))
			.ToList();
		await new TestManifestRepository(connectionFactory).WriteManifestAsync(manifest, cancellationToken);
		Console.WriteLine($"  test manifest written ({manifest.Count:N0} rows in dbo.TestManifest)");

		if (!options.HasFlag("no-batches"))
		{
			int batchCount = await inboxRepository.CreateBatchesAsync(batchSize, cancellationToken);
			Console.WriteLine($"Created {batchCount:N0} batches of up to {batchSize:N0} messages.");
		}

		return result.RejectedCount == 0 ? 0 : 1;
	}
}
