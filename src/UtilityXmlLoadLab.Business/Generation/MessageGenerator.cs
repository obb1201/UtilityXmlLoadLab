using System.Diagnostics;
using System.Globalization;
using Bogus;

namespace UtilityXmlLoadLab.Business.Generation;

/// <summary>
/// Generates synthetic SAP IS-U style messages (all data is fake, made with Bogus).
///
/// It simulates buildings (connection objects) with premises; each premise gets installations with
/// meters, then a chain of tenants: customer master, move-in (contracts + agreements), yearly
/// payment batches, meter swaps, and move-out. References are consistent, ratios are realistic
/// (many payments per account, a few installations per premise, meters replaced over time).
///
/// Edge cases: arrival order is shuffled locally (so references often point to a later message),
/// very short tenancies make a move-out arrive before its move-in, contracts with open end dates,
/// meter swaps. Optional faults are injected afterwards (see <see cref="FaultInjector"/>).
/// </summary>
public sealed class MessageGenerator
{
	private const int ARRIVAL_SHUFFLE_WINDOW = 30;
	private const double SHORT_TENANCY_RATE = 0.03;
	private const double METER_SWAP_RATE = 0.15;

	private static readonly DateTime WORLD_START = new DateTime(2015, 1, 1);
	private static readonly DateTime WORLD_END = new DateTime(2025, 6, 30);
	private static readonly string[] MANUFACTURERS = { "Acme Metering", "Contoso Instruments", "Northwind Meters", "Fabrikam Devices" };
	private static readonly string[] DIVISIONS = { "ELEC", "GAS", "WATER" };

	private readonly Faker _faker;
	private readonly Random _random;
	private long _nextCustomerNo = 1000000001;
	private long _nextAccountNo = 2000000001;
	private long _nextConnectionObjectNo = 3000000001;
	private long _nextPremiseNo = 3100000001;
	private long _nextInstallationNo = 4000000001;
	private long _nextMeterNo = 5000000001;
	private long _nextContractNo = 6000000001;
	private long _nextAgreementNo = 7000000001;
	private long _nextEventNo = 8000000001;
	private long _nextPaymentNo = 9000000001;

	private readonly List<GeneratedMessage> _messages = new List<GeneratedMessage>();
	private readonly List<ContractState> _openContracts = new List<ContractState>();
	private readonly List<InstallationState> _installations = new List<InstallationState>();

	public MessageGenerator(int seed)
	{
		_random = new Random(seed);
		_faker = new Faker("en");
		_faker.Random = new Randomizer(seed);
	}

	/// <summary>Generates exactly <see cref="GenerationOptions.MessageCount"/> unique messages, in arrival order.</summary>
	public IReadOnlyList<GeneratedMessage> GenerateMessages(GenerationOptions options)
	{
		int businessFaultCount = (int)Math.Round(options.MessageCount * options.BusinessFaultRate);

		while (_messages.Count < options.MessageCount)
		{
			GenerateConnectionObject();
		}

		for (int index = 0; index < businessFaultCount; index++)
		{
			AddOverlappingContract();
			AddMeterDoubleInstall();
		}

		List<GeneratedMessage> ordered = _messages.OrderBy(message => message.CreatedAt).ThenBy(message => message.MessageId, StringComparer.Ordinal).ToList();
		TrimPayments(ordered, options.MessageCount);
		return CreateArrivalOrder(ordered);
	}

	private void GenerateConnectionObject()
	{
		string connectionObjectId = NextId(ref _nextConnectionObjectNo);
		string street = _faker.Address.StreetName();
		string houseNo = _faker.Random.Int(1, 250).ToString(CultureInfo.InvariantCulture);
		string postalCode = _faker.Address.ZipCode("#####");
		string city = _faker.Address.City();
		int premiseCount = _faker.Random.Int(1, 4);

		for (int index = 0; index < premiseCount; index++)
		{
			string? unit = premiseCount > 1 ? (index + 1).ToString(CultureInfo.InvariantCulture) : null;
			GeneratePremise(connectionObjectId, street, houseNo, unit, postalCode, city, premiseCount > 1 ? "APT" : _faker.PickRandom("HOUSE", "SHOP"));
		}
	}

	private void GeneratePremise(string connectionObjectId, string street, string houseNo, string? unit, string postalCode, string city,
		string premiseType)
	{
		string premiseId = NextId(ref _nextPremiseNo);
		DateTime createdOn = RandomDate(WORLD_START, new DateTime(2019, 12, 31));
		int installationCount = _faker.Random.Int(1, 3);
		List<InstallationState> installations = new List<InstallationState>();

		for (int index = 0; index < installationCount; index++)
		{
			string division = DIVISIONS[index];
			string rateCategory = division == "ELEC" ? _faker.PickRandom("RES-STD", "RES-TOU") : "RES-STD";
			InstallationState installation = new InstallationState(NextId(ref _nextInstallationNo), premiseId, division, rateCategory,
				createdOn, CreateMeter(division, createdOn));
			installations.Add(installation);
			_installations.Add(installation);
		}

		string messageId = NextMessageId();
		DateTime createdAt = AddTimeOfDay(createdOn);
		using (XmlMessageBuilder builder = new XmlMessageBuilder(messageId, "PREMISE_MASTER", createdAt))
		{
			builder.StartSegment("Premise");
			builder.AddLeaf("PremiseId", premiseId);
			builder.AddLeaf("ConnectionObjectId", connectionObjectId);
			builder.AddLeaf("PremiseType", premiseType);
			builder.AddLeaf("Street", street);
			builder.AddLeaf("HouseNo", houseNo);
			builder.AddLeaf("Unit", unit);
			builder.AddLeaf("PostalCode", postalCode);
			builder.AddLeaf("City", city);
			builder.AddLeaf("Country", "US");

			foreach (InstallationState installation in installations)
			{
				builder.StartSegment("Installation");
				builder.AddLeaf("InstallationId", installation.InstallationId);
				builder.AddLeaf("Division", installation.Division);
				builder.AddLeaf("RateCategory", installation.RateCategory);
				builder.AddDate("ValidFrom", installation.ValidFrom);
				WriteMeter(builder, "Meter", installation.CurrentMeter, null);
				builder.EndElement();
			}

			builder.EndElement();
			AddMessage(builder, messageId, "PREMISE_MASTER", createdAt);
		}

		DateTime moveIn = createdOn.AddDays(_faker.Random.Int(5, 90));
		while (moveIn < WORLD_END.AddDays(-30))
		{
			moveIn = GenerateTenancy(premiseId, installations, moveIn);
		}
	}

	/// <returns>The move-in date of the next tenant (after WORLD_END when the tenancy stays open).</returns>
	private DateTime GenerateTenancy(string premiseId, List<InstallationState> installations, DateTime moveIn)
	{
		(string customerId, string accountId, string paymentMethod) = AddCustomerMaster(moveIn);

		List<ContractState> contracts = installations
			.Select(installation => new ContractState(NextId(ref _nextContractNo), accountId, customerId, premiseId, installation, moveIn))
			.ToList();

		string moveInMessageId = AddMoveMessage("MOVE_IN", customerId, accountId, premiseId, moveIn, contracts, null, true);

		bool bIsShortTenancy = _faker.Random.Double() < SHORT_TENANCY_RATE;
		DateTime moveOut = bIsShortTenancy ? moveIn.AddDays(_faker.Random.Int(7, 25)) : moveIn.AddDays(_faker.Random.Int(365, 2920));
		DateTime tenancyEnd = moveOut < WORLD_END ? moveOut : WORLD_END;

		AddPayments(accountId, paymentMethod, moveIn, tenancyEnd);

		foreach (InstallationState installation in installations.Where(item => _faker.Random.Double() < METER_SWAP_RATE))
		{
			if ((tenancyEnd - moveIn).TotalDays > 60)
			{
				DateTime swapDate = RandomDate(moveIn.AddDays(30), tenancyEnd.AddDays(-15));
				AddMeterSwap(installation, swapDate);
			}
		}

		if (moveOut >= WORLD_END)
		{
			_openContracts.AddRange(contracts);
			return WORLD_END.AddDays(1);
		}

		string moveOutMessageId = AddMoveMessage("MOVE_OUT", customerId, accountId, premiseId, moveOut, contracts, moveOut, false);
		if (bIsShortTenancy)
		{
			// Edge case: the move-out message arrives before the move-in message.
			System.Diagnostics.Debug.Assert(_messages[_messages.Count - 1].MessageId == moveOutMessageId);
			_messages[_messages.Count - 1].ArriveBeforeMessageIdOrNull = moveInMessageId;
		}

		return moveOut.AddDays(_faker.Random.Int(1, 30));
	}

	private (string CustomerId, string AccountId, string PaymentMethod) AddCustomerMaster(DateTime moveIn)
	{
		string customerId = NextId(ref _nextCustomerNo);
		string messageId = NextMessageId();
		DateTime createdAt = AddTimeOfDay(moveIn.AddDays(-_faker.Random.Int(0, 3)));
		bool bIsOrganisation = _faker.Random.Double() < 0.1;
		string firstName = _faker.Name.FirstName();
		string lastName = _faker.Name.LastName();
		string accountId = NextId(ref _nextAccountNo);
		string paymentMethod = _faker.PickRandom("DD", "TRANSFER", "CARD");

		using XmlMessageBuilder builder = new XmlMessageBuilder(messageId, "CUSTOMER_MASTER", createdAt);
		builder.StartSegment("Customer");
		builder.AddLeaf("CustomerId", customerId);
		builder.AddLeaf("Category", bIsOrganisation ? "ORG" : "PERSON");
		builder.AddLeaf("FirstName", bIsOrganisation ? null : firstName);
		builder.AddLeaf("LastName", bIsOrganisation ? null : lastName);
		builder.AddLeaf("OrgName", bIsOrganisation ? Truncate(_faker.Company.CompanyName(), 120) : null);
		builder.AddLeaf("Email", Truncate(_faker.Internet.Email(firstName, lastName, "example.com"), 120));
		builder.AddLeaf("Phone", _faker.Phone.PhoneNumber("###-###-####"));
		builder.AddDate("ValidFrom", createdAt.Date);

		WriteAddress(builder, "HOME");
		if (_faker.Random.Double() < 0.3)
		{
			WriteAddress(builder, "BILLING");
		}

		builder.StartSegment("Account");
		builder.AddLeaf("AccountId", accountId);
		builder.AddLeaf("AccountClass", bIsOrganisation ? "BUS" : "RES");
		builder.AddLeaf("BillingCycle", _faker.PickRandom("MONTHLY", "QUARTERLY"));
		builder.AddLeaf("PaymentMethod", paymentMethod);
		builder.AddDecimal("CreditLimit", _faker.Random.Double() < 0.7 ? Math.Round(_faker.Random.Decimal(100M, 5000M), 2) : null);
		builder.AddDate("OpenedOn", createdAt.Date);
		builder.EndElement();

		builder.EndElement();
		AddMessage(builder, messageId, "CUSTOMER_MASTER", createdAt);
		return (customerId, accountId, paymentMethod);
	}

	/// <returns>The new message id.</returns>
	private string AddMoveMessage(string eventType, string customerId, string accountId, string premiseId, DateTime eventDate,
		IReadOnlyList<ContractState> contracts, DateTime? endDateOrNull, bool bWithAgreements)
	{
		string messageId = NextMessageId();
		DateTime createdAt = AddTimeOfDay(eventDate);

		using XmlMessageBuilder builder = new XmlMessageBuilder(messageId, eventType, createdAt);
		builder.StartSegment("MoveEvent");
		builder.AddLeaf("EventId", NextId(ref _nextEventNo));
		builder.AddLeaf("EventType", eventType);
		builder.AddLeaf("CustomerId", customerId);
		builder.AddLeaf("AccountId", accountId);
		builder.AddLeaf("PremiseId", premiseId);
		builder.AddDate("EventDate", eventDate);
		builder.EndElement();

		foreach (ContractState contract in contracts)
		{
			builder.StartSegment("Contract");
			builder.AddLeaf("ContractId", contract.ContractId);
			builder.AddLeaf("AccountId", contract.AccountId);
			builder.AddLeaf("InstallationId", contract.Installation.InstallationId);
			builder.AddDate("StartDate", contract.StartDate);
			builder.AddDate("EndDate", endDateOrNull);

			if (bWithAgreements)
			{
				WriteAgreement(builder, "ENERGY", contract.StartDate);
				if (_faker.Random.Double() < 0.4)
				{
					WriteAgreement(builder, _faker.PickRandom("SERVICE", "DISCOUNT"), contract.StartDate);
				}
			}

			builder.EndElement();
		}

		AddMessage(builder, messageId, eventType, createdAt);
		return messageId;
	}

	/// <summary>One PAYMENT_BATCH message per account and calendar year, about one payment per month.</summary>
	private void AddPayments(string accountId, string paymentMethod, DateTime moveIn, DateTime tenancyEnd)
	{
		DateTime paymentDate = moveIn.AddDays(30);

		while (paymentDate <= tenancyEnd)
		{
			int year = paymentDate.Year;
			List<(DateTime Date, decimal Amount)> payments = new List<(DateTime, decimal)>();

			while (paymentDate <= tenancyEnd && paymentDate.Year == year)
			{
				payments.Add((paymentDate, Math.Round(_faker.Random.Decimal(20M, 400M), 2)));
				paymentDate = paymentDate.AddDays(_faker.Random.Int(26, 34));
			}

			DateTime lastPaymentDate = payments[payments.Count - 1].Date;
			DateTime createdAt = AddTimeOfDay(lastPaymentDate.AddDays(1) > WORLD_END ? WORLD_END : lastPaymentDate.AddDays(1));
			string messageId = NextMessageId();

			using XmlMessageBuilder builder = new XmlMessageBuilder(messageId, "PAYMENT_BATCH", createdAt);
			foreach ((DateTime date, decimal amount) in payments)
			{
				builder.StartSegment("Payment");
				builder.AddLeaf("PaymentId", NextId(ref _nextPaymentNo));
				builder.AddLeaf("AccountId", accountId);
				builder.AddDecimal("Amount", amount);
				builder.AddLeaf("Currency", "USD");
				builder.AddDate("PaymentDate", date);
				builder.AddLeaf("PaymentMethod", paymentMethod);
				builder.AddLeaf("Reference", "INV-" + date.ToString("yyyyMM", CultureInfo.InvariantCulture) + "-" + accountId);
				builder.EndElement();
			}

			AddMessage(builder, messageId, "PAYMENT_BATCH", createdAt);
		}
	}

	private void AddMeterSwap(InstallationState installation, DateTime swapDate)
	{
		MeterState removedMeter = installation.CurrentMeter;
		MeterState installedMeter = CreateMeter(installation.Division, swapDate);
		installation.CurrentMeter = installedMeter;

		string messageId = NextMessageId();
		DateTime createdAt = AddTimeOfDay(swapDate);

		using XmlMessageBuilder builder = new XmlMessageBuilder(messageId, "METER_SWAP", createdAt);
		builder.StartGroup("MeterSwap");
		builder.AddLeaf("InstallationId", installation.InstallationId);
		builder.AddDate("SwapDate", swapDate);
		WriteMeter(builder, "RemovedMeter", removedMeter, swapDate);
		WriteMeter(builder, "InstalledMeter", installedMeter, null);
		builder.EndElement();
		AddMessage(builder, messageId, "METER_SWAP", createdAt);
	}

	/// <summary>Business-rule fault: a second open contract on an installation that already has one.</summary>
	private void AddOverlappingContract()
	{
		if (_openContracts.Count == 0)
		{
			return;
		}

		ContractState existing = _faker.PickRandom(_openContracts);
		DateTime startDate = RandomDate(existing.StartDate.AddDays(1), WORLD_END.AddDays(-1));
		ContractState overlapping = new ContractState(NextId(ref _nextContractNo), existing.AccountId, existing.CustomerId,
			existing.PremiseId, existing.Installation, startDate);

		string messageId = AddMoveMessage("MOVE_IN", existing.CustomerId, existing.AccountId, existing.PremiseId, startDate,
			new[] { overlapping }, null, true);
		_messages[_messages.Count - 1].FaultKind = EFaultKind.OverlapContract;
		Debug.Assert(_messages[_messages.Count - 1].MessageId == messageId);
	}

	/// <summary>Business-rule fault: a meter installed in a second installation while still installed in the first.</summary>
	private void AddMeterDoubleInstall()
	{
		if (_installations.Count < 2)
		{
			return;
		}

		InstallationState source = _faker.PickRandom(_installations);
		InstallationState target = _faker.PickRandom(_installations);
		if (source.InstallationId == target.InstallationId)
		{
			return;
		}

		MeterState meter = source.CurrentMeter;
		DateTime installDate = RandomDate((meter.InstallDate > target.ValidFrom ? meter.InstallDate : target.ValidFrom).AddDays(1), WORLD_END.AddDays(-1));
		MeterState moved = new MeterState(meter.MeterId, meter.SerialNumber, meter.MeterType, meter.Manufacturer, installDate, meter.Registers);

		string messageId = NextMessageId();
		DateTime createdAt = AddTimeOfDay(installDate);

		using XmlMessageBuilder builder = new XmlMessageBuilder(messageId, "METER_SWAP", createdAt);
		builder.StartGroup("MeterSwap");
		builder.AddLeaf("InstallationId", target.InstallationId);
		builder.AddDate("SwapDate", installDate);
		WriteMeter(builder, "InstalledMeter", moved, null);
		builder.EndElement();
		AddMessage(builder, messageId, "METER_SWAP", createdAt);
		_messages[_messages.Count - 1].FaultKind = EFaultKind.MeterDoubleInstall;
	}

	private MeterState CreateMeter(string division, DateTime installDate)
	{
		List<RegisterState> registers = new List<RegisterState>();
		string meterType;

		switch (division)
		{
			case "ELEC":
				meterType = _faker.PickRandom("ELEC-1PH", "ELEC-3PH");
				if (_faker.Random.Double() < 0.4)
				{
					registers.Add(new RegisterState("001", "DAY", "KWH", 1));
					registers.Add(new RegisterState("002", "NIGHT", "KWH", 1));
				}
				else
				{
					registers.Add(new RegisterState("001", "CONS", "KWH", 1));
				}

				break;
			case "GAS":
				meterType = "GAS-G4";
				registers.Add(new RegisterState("001", "CONS", "M3", 3));
				break;
			case "WATER":
				meterType = "WATER-15";
				registers.Add(new RegisterState("001", "CONS", "M3", 3));
				break;
			default:
				Debug.Fail("Unknown division " + division);
				meterType = "UNKNOWN";
				break;
		}

		return new MeterState(NextId(ref _nextMeterNo), "SN" + _faker.Random.ReplaceNumbers("##########"), meterType,
			_faker.PickRandom(MANUFACTURERS), installDate, registers);
	}

	private void WriteMeter(XmlMessageBuilder builder, string elementName, MeterState meter, DateTime? removeDateOrNull)
	{
		builder.StartSegment(elementName);
		builder.AddLeaf("MeterId", meter.MeterId);
		builder.AddLeaf("SerialNumber", meter.SerialNumber);
		builder.AddLeaf("MeterType", meter.MeterType);
		builder.AddLeaf("Manufacturer", meter.Manufacturer);
		builder.AddDate("InstallDate", meter.InstallDate);
		builder.AddDate("RemoveDate", removeDateOrNull);

		if (removeDateOrNull == null)
		{
			foreach (RegisterState register in meter.Registers)
			{
				builder.StartSegment("Register");
				builder.AddLeaf("RegisterNo", register.RegisterNo);
				builder.AddLeaf("RegisterType", register.RegisterType);
				builder.AddLeaf("UnitOfMeasure", register.UnitOfMeasure);
				builder.AddLeaf("Decimals", register.Decimals.ToString(CultureInfo.InvariantCulture));
				builder.EndElement();
			}
		}

		builder.EndElement();
	}

	private void WriteAddress(XmlMessageBuilder builder, string addressType)
	{
		builder.StartSegment("Address");
		builder.AddLeaf("AddressType", addressType);
		builder.AddLeaf("Street", Truncate(_faker.Address.StreetName(), 100));
		builder.AddLeaf("HouseNo", _faker.Random.Int(1, 250).ToString(CultureInfo.InvariantCulture));
		builder.AddLeaf("PostalCode", _faker.Address.ZipCode("#####"));
		builder.AddLeaf("City", Truncate(_faker.Address.City(), 60));
		builder.AddLeaf("Country", "US");
		builder.EndElement();
	}

	private void WriteAgreement(XmlMessageBuilder builder, string agreementType, DateTime validFrom)
	{
		builder.StartSegment("Agreement");
		builder.AddLeaf("AgreementId", NextId(ref _nextAgreementNo));
		builder.AddLeaf("AgreementType", agreementType);
		builder.AddLeaf("PriceLevel", _faker.PickRandom("STANDARD", "GREEN", "FIXED12"));
		builder.AddDecimal("DiscountPct", agreementType == "DISCOUNT" ? Math.Round(_faker.Random.Decimal(1M, 15M), 2) : null);
		builder.AddDate("ValidFrom", validFrom);
		builder.AddDate("ValidTo", agreementType == "DISCOUNT" ? validFrom.AddYears(1) : null);
		builder.EndElement();
	}

	private void AddMessage(XmlMessageBuilder builder, string messageId, string messageType, DateTime createdAt)
	{
		string xml = builder.ToXml();
		_messages.Add(new GeneratedMessage(messageId, messageType, createdAt, builder.SegmentCount, xml, EFaultKind.None, 1));
	}

	/// <summary>Removes the latest payment messages until exactly <paramref name="messageCount"/> remain.</summary>
	private static void TrimPayments(List<GeneratedMessage> ordered, int messageCount)
	{
		for (int index = ordered.Count - 1; index >= 0 && ordered.Count > messageCount; index--)
		{
			if (ordered[index].MessageType == "PAYMENT_BATCH")
			{
				ordered.RemoveAt(index);
			}
		}
	}

	/// <summary>Messages arrive roughly in creation order, shuffled within a small window.</summary>
	private List<GeneratedMessage> CreateArrivalOrder(List<GeneratedMessage> ordered)
	{
		Dictionary<string, double> arrivalKeys = new Dictionary<string, double>(ordered.Count, StringComparer.Ordinal);
		for (int index = 0; index < ordered.Count; index++)
		{
			arrivalKeys[ordered[index].MessageId] = index + _random.NextDouble() * ARRIVAL_SHUFFLE_WINDOW;
		}

		// Forced edge case: put the message just before the one it must precede.
		foreach (GeneratedMessage message in ordered.Where(item => item.ArriveBeforeMessageIdOrNull != null))
		{
			if (arrivalKeys.TryGetValue(message.ArriveBeforeMessageIdOrNull!, out double targetKey))
			{
				arrivalKeys[message.MessageId] = targetKey - 0.000001;
			}
		}

		return ordered.OrderBy(message => arrivalKeys[message.MessageId]).ToList();
	}

	private string NextMessageId()
	{
		byte[] bytes = new byte[16];
		_random.NextBytes(bytes);
		return new Guid(bytes).ToString("N").ToUpperInvariant();
	}

	private static string NextId(ref long counter)
	{
		long value = counter;
		counter++;
		return value.ToString(CultureInfo.InvariantCulture);
	}

	private DateTime RandomDate(DateTime from, DateTime to)
	{
		if (to <= from)
		{
			return from;
		}

		int days = (int)(to - from).TotalDays;
		return from.AddDays(_random.Next(0, days + 1));
	}

	private DateTime AddTimeOfDay(DateTime date)
	{
		return date.Date.AddSeconds(_random.Next(6 * 3600, 22 * 3600));
	}

	private static string Truncate(string value, int maxLength)
	{
		return value.Length <= maxLength ? value : value.Substring(0, maxLength);
	}
}
