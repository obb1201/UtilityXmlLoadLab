using UtilityXmlLoadLab.Business.Generation;
using UtilityXmlLoadLab.Business.Parsing;
using UtilityXmlLoadLab.Core.Messages;

namespace UtilityXmlLoadLab.Tests;

/// <summary>The generator and the parser must agree: clean messages pass, faulted messages fail.</summary>
public class GeneratorTests
{
	private const int MESSAGE_COUNT = 3000;

	[Fact]
	public void GenerateMessages_SameSeed_GivesSameMessages()
	{
		IReadOnlyList<GeneratedMessage> first = Generate(7, 0.0, 0.0);
		IReadOnlyList<GeneratedMessage> second = Generate(7, 0.0, 0.0);

		Assert.Equal(first.Select(message => message.Xml), second.Select(message => message.Xml));
	}

	[Fact]
	public void GenerateMessages_CleanData_AllPassWithExpectedRowCounts()
	{
		IReadOnlyList<GeneratedMessage> messages = Generate(11, 0.0, 0.002);
		XmlMessageParser parser = new XmlMessageParser();

		Assert.Equal(MESSAGE_COUNT, messages.Count);
		Assert.Equal(MESSAGE_COUNT, messages.Select(message => message.MessageId).Distinct().Count());

		foreach (GeneratedMessage message in messages)
		{
			MessageResult result = parser.ParseMessage(new RawMessage(message.MessageId, DateTime.UtcNow, message.Xml));
			Assert.True(result.Status == EValidationStatus.Passed, message.MessageId + ": " + result.ErrorDetail);
			Assert.Equal(message.ExpectedRowCount, result.Rows.Count);
		}
	}

	[Fact]
	public void InjectFaults_EveryTechnicalFaultFails_AndDuplicatesAreAdded()
	{
		IReadOnlyList<GeneratedMessage> messages = Generate(13, 0.0, 0.0);
		List<GeneratedMessage> arrivals = new FaultInjector(14).InjectFaults(messages, 0.02);
		XmlMessageParser parser = new XmlMessageParser();

		int faultCount = (int)Math.Round(MESSAGE_COUNT * 0.02);
		Assert.Equal(MESSAGE_COUNT + faultCount, arrivals.Count);
		Assert.Equal(faultCount, arrivals.Count(message => message.ArrivalNo == 2));

		foreach (GeneratedMessage message in messages)
		{
			MessageResult result = parser.ParseMessage(new RawMessage(message.MessageId, DateTime.UtcNow, message.Xml));
			bool bShouldFail = message.FaultKind is EFaultKind.Malformed or EFaultKind.MissingRequired or EFaultKind.BadValue;
			Assert.True(bShouldFail == (result.Status == EValidationStatus.Failed), message.FaultKind + " " + message.MessageId);
		}

		Assert.Equal(faultCount, messages.Count(message => message.FaultKind == EFaultKind.Malformed));
		Assert.Equal(faultCount, messages.Count(message => message.FaultKind == EFaultKind.MissingRequired));
		Assert.Equal(faultCount, messages.Count(message => message.FaultKind == EFaultKind.BadValue));
	}

	[Fact]
	public void GenerateMessages_ContainsTheEdgeCases()
	{
		IReadOnlyList<GeneratedMessage> messages = Generate(17, 0.0, 0.0);
		Dictionary<string, int> arrivalIndex = messages.Select((message, index) => (message.MessageId, index))
			.ToDictionary(item => item.MessageId, item => item.index);

		// A move-out that arrives before its move-in.
		Assert.Contains(messages, message => message.ArriveBeforeMessageIdOrNull != null
			&& arrivalIndex[message.MessageId] < arrivalIndex[message.ArriveBeforeMessageIdOrNull]);

		// Meter swaps and contracts with an open end date.
		Assert.Contains(messages, message => message.MessageType == "METER_SWAP");
		Assert.Contains(messages, message => message.MessageType == "MOVE_IN" && !message.Xml.Contains("<EndDate>", StringComparison.Ordinal));
	}

	private static IReadOnlyList<GeneratedMessage> Generate(int seed, double faultRate, double businessFaultRate)
	{
		return new MessageGenerator(seed).GenerateMessages(new GenerationOptions(MESSAGE_COUNT, seed, faultRate, businessFaultRate));
	}
}
