using System.ServiceModel;
using UtilityXmlLoadLab.Business.Generation;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Business.Delivery;

/// <summary>Delivers generated messages to the Inbox: through the CoreWCF endpoint, or straight into the table.</summary>
public sealed class MessageSender
{
	private const int DIRECT_CHUNK_SIZE = 5000;

	private readonly InboxRepository _inboxRepository;

	public MessageSender(InboxRepository inboxRepository)
	{
		_inboxRepository = inboxRepository;
	}

	/// <summary>Fast path for tests: bulk insert in chunks, same duplicate rules as the receiver.</summary>
	public async Task<DeliveryResult> SendDirectAsync(IReadOnlyList<GeneratedMessage> messages, CancellationToken cancellationToken)
	{
		int acceptedCount = 0;
		int duplicateCount = 0;

		for (int start = 0; start < messages.Count; start += DIRECT_CHUNK_SIZE)
		{
			List<(string MessageId, string RawXml)> chunk = messages
				.Skip(start)
				.Take(DIRECT_CHUNK_SIZE)
				.Select(message => (message.MessageId, message.Xml))
				.ToList();

			(int accepted, int duplicates) = await _inboxRepository.ReceiveBulkAsync(chunk, cancellationToken);
			acceptedCount += accepted;
			duplicateCount += duplicates;
		}

		return new DeliveryResult(acceptedCount, duplicateCount, 0, new List<string>());
	}

	/// <summary>Sends each message to the CoreWCF endpoint, several at a time.</summary>
	public static async Task<DeliveryResult> SendToServiceAsync(IReadOnlyList<GeneratedMessage> messages, Uri endpoint,
		int degreeOfParallelism, CancellationToken cancellationToken)
	{
		BasicHttpBinding binding = new BasicHttpBinding();
		binding.MaxReceivedMessageSize = InboxContract.MAX_MESSAGE_BYTES;
		binding.SendTimeout = TimeSpan.FromMinutes(2);

		ChannelFactory<IInboxServiceClient> factory = new ChannelFactory<IInboxServiceClient>(binding, new EndpointAddress(endpoint));
		IInboxServiceClient client = factory.CreateChannel();

		int acceptedCount = 0;
		int duplicateCount = 0;
		int rejectedCount = 0;
		List<string> problems = new List<string>();

		ParallelOptions options = new ParallelOptions();
		options.MaxDegreeOfParallelism = degreeOfParallelism;
		options.CancellationToken = cancellationToken;

		try
		{
			await Parallel.ForEachAsync(messages, options, async (message, token) =>
			{
				string outcome = await client.SubmitMessageAsync(message.MessageId, message.Xml);

				if (outcome == InboxContract.OUTCOME_ACCEPTED)
				{
					Interlocked.Increment(ref acceptedCount);
				}
				else if (outcome == InboxContract.OUTCOME_DUPLICATE)
				{
					Interlocked.Increment(ref duplicateCount);
				}
				else
				{
					Interlocked.Increment(ref rejectedCount);
					lock (problems)
					{
						problems.Add(message.MessageId + ": " + outcome);
					}
				}
			});
		}
		finally
		{
			((IClientChannel)client).Close();
			factory.Close();
		}

		return new DeliveryResult(acceptedCount, duplicateCount, rejectedCount, problems);
	}
}
