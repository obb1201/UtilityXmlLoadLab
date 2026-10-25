using CoreWCF;
using Microsoft.Data.SqlClient;
using UtilityXmlLoadLab.Business.Delivery;
using UtilityXmlLoadLab.DataAccess;

namespace UtilityXmlLoadLab.Receiver;

/// <summary>
/// Stores MessageId + raw XML + ReceivedAt in dbo.Inbox and returns. No parsing in the request
/// path. A duplicate MessageId is recorded in dbo.InboxDuplicate and still returns success.
/// </summary>
[ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
public sealed class InboxService : IInboxService
{
	private readonly InboxRepository _inboxRepository;
	private readonly ILogger<InboxService> _logger;

	public InboxService(InboxRepository inboxRepository, ILogger<InboxService> logger)
	{
		_inboxRepository = inboxRepository;
		_logger = logger;
	}

	public async Task<string> SubmitMessageAsync(string messageId, string rawXml)
	{
		// Boundary validation: the only checks done here. The XML itself is not inspected.
		if (string.IsNullOrWhiteSpace(messageId))
		{
			return InboxContract.OUTCOME_REJECTED_PREFIX + "messageId is required.";
		}

		if (messageId.Length > InboxContract.MAX_MESSAGE_ID_LENGTH)
		{
			return InboxContract.OUTCOME_REJECTED_PREFIX + "messageId is longer than " + InboxContract.MAX_MESSAGE_ID_LENGTH + " characters.";
		}

		if (string.IsNullOrEmpty(rawXml))
		{
			return InboxContract.OUTCOME_REJECTED_PREFIX + "rawXml is required.";
		}

		try
		{
			return await _inboxRepository.ReceiveMessageAsync(messageId, rawXml, CancellationToken.None);
		}
		catch (SqlException exception)
		{
			// The sender must know the message was NOT stored, so it can retry: return a fault.
			_logger.LogError(exception, "Could not store message {MessageId}", messageId);
			throw new FaultException("The inbox is not available. The message was not stored; please retry.");
		}
	}
}
