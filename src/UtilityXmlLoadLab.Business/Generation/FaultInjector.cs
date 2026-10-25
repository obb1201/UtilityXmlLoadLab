using System.Text.RegularExpressions;

namespace UtilityXmlLoadLab.Business.Generation;

/// <summary>
/// Injects technical faults into a generated message list (about <c>faultRate</c> of the messages
/// for each kind): malformed XML, a missing required element, a bad date or decimal, and duplicate
/// arrivals of a MessageId. Faults are applied to clean messages only, so every message has at most
/// one fault and the test manifest can say exactly what each message should produce.
/// </summary>
public sealed class FaultInjector
{
	private const int MAX_DUPLICATE_DELAY = 300;

	private static readonly string[] REQUIRED_ELEMENTS =
	{
		"ValidFrom", "Street", "OpenedOn", "InstallDate", "SerialNumber", "StartDate", "EventDate", "Amount", "PaymentDate", "RegisterNo"
	};

	private static readonly string[] DECIMAL_ELEMENTS = { "Amount", "CreditLimit", "DiscountPct" };
	private static readonly string[] DATE_ELEMENTS = { "PaymentDate", "StartDate", "InstallDate", "OpenedOn", "EventDate", "ValidFrom" };
	private static readonly string[] BAD_DECIMALS = { "12,50", "1.2.3", "abc", "9e5" };
	private static readonly string[] BAD_DATES = { "2023-02-30", "31/12/2023", "2023-13-01", "yesterday" };

	private readonly Random _random;

	public FaultInjector(int seed)
	{
		_random = new Random(seed);
	}

	/// <summary>Changes messages in place and returns the arrival list including duplicate arrivals.</summary>
	public List<GeneratedMessage> InjectFaults(IReadOnlyList<GeneratedMessage> messages, double faultRate)
	{
		int faultCount = (int)Math.Round(messages.Count * faultRate);
		List<int> cleanIndexes = Enumerable.Range(0, messages.Count).Where(index => messages[index].FaultKind == EFaultKind.None).ToList();
		Shuffle(cleanIndexes);

		int next = 0;
		for (int count = 0; count < faultCount && next < cleanIndexes.Count; count++)
		{
			MakeMalformed(messages[cleanIndexes[next++]]);
		}

		for (int count = 0; count < faultCount && next < cleanIndexes.Count; count++)
		{
			if (!TryRemoveRequired(messages[cleanIndexes[next++]]))
			{
				count--;
			}
		}

		for (int count = 0; count < faultCount && next < cleanIndexes.Count; count++)
		{
			if (!TrySetBadValue(messages[cleanIndexes[next++]]))
			{
				count--;
			}
		}

		return AddDuplicateArrivals(messages, faultCount);
	}

	private void MakeMalformed(GeneratedMessage message)
	{
		int cutAt = (int)(message.Xml.Length * (0.3 + _random.NextDouble() * 0.6));
		message.Xml = message.Xml.Substring(0, cutAt);
		message.FaultKind = EFaultKind.Malformed;
	}

	private bool TryRemoveRequired(GeneratedMessage message)
	{
		string[] present = REQUIRED_ELEMENTS.Where(name => message.Xml.Contains("<" + name + ">", StringComparison.Ordinal)).ToArray();
		if (present.Length == 0)
		{
			return false;
		}

		string name = present[_random.Next(present.Length)];
		Regex leaf = new Regex("<" + name + ">[^<]*</" + name + ">");
		message.Xml = leaf.Replace(message.Xml, string.Empty, 1);
		message.FaultKind = EFaultKind.MissingRequired;
		return true;
	}

	private bool TrySetBadValue(GeneratedMessage message)
	{
		bool bUseDecimal = _random.Next(2) == 0;
		string[] candidates = bUseDecimal ? DECIMAL_ELEMENTS : DATE_ELEMENTS;
		string[] present = candidates.Where(name => message.Xml.Contains("<" + name + ">", StringComparison.Ordinal)).ToArray();

		if (present.Length == 0)
		{
			bUseDecimal = !bUseDecimal;
			candidates = bUseDecimal ? DECIMAL_ELEMENTS : DATE_ELEMENTS;
			present = candidates.Where(name => message.Xml.Contains("<" + name + ">", StringComparison.Ordinal)).ToArray();
		}

		if (present.Length == 0)
		{
			return false;
		}

		string name = present[_random.Next(present.Length)];
		string badValue = bUseDecimal ? BAD_DECIMALS[_random.Next(BAD_DECIMALS.Length)] : BAD_DATES[_random.Next(BAD_DATES.Length)];
		Regex leaf = new Regex("<" + name + ">[^<]*</" + name + ">");
		message.Xml = leaf.Replace(message.Xml, "<" + name + ">" + badValue + "</" + name + ">", 1);
		message.FaultKind = EFaultKind.BadValue;
		return true;
	}

	/// <summary>Re-sends some messages (same MessageId, same content) a little later.</summary>
	private List<GeneratedMessage> AddDuplicateArrivals(IReadOnlyList<GeneratedMessage> messages, int duplicateCount)
	{
		List<int> indexes = Enumerable.Range(0, messages.Count).ToList();
		Shuffle(indexes);

		// Each duplicate arrives after the message at position (original + delay).
		ILookup<int, GeneratedMessage> duplicatesAfter = indexes.Take(duplicateCount)
			.ToLookup(index => Math.Min(index + _random.Next(MAX_DUPLICATE_DELAY), messages.Count - 1),
				index => messages[index].CreateDuplicateArrival(2));

		List<GeneratedMessage> arrival = new List<GeneratedMessage>(messages.Count + duplicateCount);
		for (int index = 0; index < messages.Count; index++)
		{
			arrival.Add(messages[index]);
			arrival.AddRange(duplicatesAfter[index]);
		}

		return arrival;
	}

	private void Shuffle(List<int> values)
	{
		for (int index = values.Count - 1; index > 0; index--)
		{
			int swapIndex = _random.Next(index + 1);
			(values[index], values[swapIndex]) = (values[swapIndex], values[index]);
		}
	}
}
