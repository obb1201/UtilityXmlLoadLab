namespace UtilityXmlLoadLab.Core.Mapping;

/// <summary>
/// The XML-to-staging mapping used by approach B (.NET).
///
/// This is the one place to change when the real XML differs from the spec. Approach A (T-SQL)
/// holds the same mapping in sql/050_ApproachA_ShredBatch.sql; keep both in step. The benchmark
/// compares the output of both approaches and reports any difference.
/// </summary>
public static class StagingMap
{
	public const string ROOT_ELEMENT = "SapMessage";
	public const string HEADER_PATH = "/SapMessage/Header";

	public static readonly IReadOnlyList<string> MESSAGE_TYPES = new[]
	{
		"CUSTOMER_MASTER", "PREMISE_MASTER", "MOVE_IN", "MOVE_OUT", "METER_SWAP", "PAYMENT_BATCH"
	};

	public static readonly IReadOnlyList<string> EVENT_TYPES = new[] { "MOVE_IN", "MOVE_OUT" };

	/// <summary>
	/// Every element name that is a segment (becomes a row). Used to count the expected rows of a
	/// message independently of the exact paths below.
	/// </summary>
	public static readonly IReadOnlySet<string> SEGMENT_ELEMENT_NAMES = new HashSet<string>(StringComparer.Ordinal)
	{
		"Header", "Customer", "Address", "Account", "Premise", "Installation", "Meter", "RemovedMeter",
		"InstalledMeter", "Register", "MoveEvent", "Contract", "Agreement", "Payment"
	};

	public static readonly StagingTable MessageHeader = new StagingTable("MessageHeader", new[]
	{
		StagingColumn.CreateText("MessageType", 30),
		StagingColumn.CreateTyped("CreatedAt", EValueType.DateTime),
		StagingColumn.CreateText("Sender", 30)
	});

	public static readonly StagingTable Customer = new StagingTable("Customer", new[]
	{
		StagingColumn.CreateText("CustomerId", 20),
		StagingColumn.CreateText("Category", 10),
		StagingColumn.CreateText("FirstName", 80),
		StagingColumn.CreateText("LastName", 80),
		StagingColumn.CreateText("OrgName", 120),
		StagingColumn.CreateText("Email", 120),
		StagingColumn.CreateText("Phone", 30),
		StagingColumn.CreateTyped("ValidFrom", EValueType.Date)
	});

	public static readonly StagingTable CustomerAddress = new StagingTable("CustomerAddress", new[]
	{
		StagingColumn.CreateText("CustomerId", 20),
		StagingColumn.CreateText("AddressType", 10),
		StagingColumn.CreateText("Street", 100),
		StagingColumn.CreateText("HouseNo", 10),
		StagingColumn.CreateText("PostalCode", 10),
		StagingColumn.CreateText("City", 60),
		StagingColumn.CreateText("Country", 2)
	});

	public static readonly StagingTable Account = new StagingTable("Account", new[]
	{
		StagingColumn.CreateText("AccountId", 20),
		StagingColumn.CreateText("CustomerId", 20),
		StagingColumn.CreateText("AccountClass", 10),
		StagingColumn.CreateText("BillingCycle", 10),
		StagingColumn.CreateText("PaymentMethod", 10),
		StagingColumn.CreateTyped("CreditLimit", EValueType.Decimal18x2),
		StagingColumn.CreateTyped("OpenedOn", EValueType.Date)
	});

	public static readonly StagingTable Premise = new StagingTable("Premise", new[]
	{
		StagingColumn.CreateText("PremiseId", 20),
		StagingColumn.CreateText("ConnectionObjectId", 20),
		StagingColumn.CreateText("PremiseType", 10),
		StagingColumn.CreateText("Street", 100),
		StagingColumn.CreateText("HouseNo", 10),
		StagingColumn.CreateText("Unit", 10),
		StagingColumn.CreateText("PostalCode", 10),
		StagingColumn.CreateText("City", 60),
		StagingColumn.CreateText("Country", 2)
	});

	public static readonly StagingTable Installation = new StagingTable("Installation", new[]
	{
		StagingColumn.CreateText("InstallationId", 20),
		StagingColumn.CreateText("PremiseId", 20),
		StagingColumn.CreateText("Division", 10),
		StagingColumn.CreateText("RateCategory", 10),
		StagingColumn.CreateTyped("ValidFrom", EValueType.Date)
	});

	public static readonly StagingTable Meter = new StagingTable("Meter", new[]
	{
		StagingColumn.CreateText("MeterId", 20),
		StagingColumn.CreateText("InstallationId", 20),
		StagingColumn.CreateText("SerialNumber", 30),
		StagingColumn.CreateText("MeterType", 10),
		StagingColumn.CreateText("Manufacturer", 40),
		StagingColumn.CreateTyped("InstallDate", EValueType.Date),
		StagingColumn.CreateTyped("RemoveDate", EValueType.Date)
	});

	public static readonly StagingTable MeterRegister = new StagingTable("MeterRegister", new[]
	{
		StagingColumn.CreateText("MeterId", 20),
		StagingColumn.CreateText("RegisterNo", 10),
		StagingColumn.CreateText("RegisterType", 10),
		StagingColumn.CreateText("UnitOfMeasure", 10),
		StagingColumn.CreateTyped("Decimals", EValueType.Integer)
	});

	public static readonly StagingTable Contract = new StagingTable("Contract", new[]
	{
		StagingColumn.CreateText("ContractId", 20),
		StagingColumn.CreateText("AccountId", 20),
		StagingColumn.CreateText("InstallationId", 20),
		StagingColumn.CreateTyped("StartDate", EValueType.Date),
		StagingColumn.CreateTyped("EndDate", EValueType.Date)
	});

	public static readonly StagingTable Agreement = new StagingTable("Agreement", new[]
	{
		StagingColumn.CreateText("AgreementId", 20),
		StagingColumn.CreateText("ContractId", 20),
		StagingColumn.CreateText("AgreementType", 10),
		StagingColumn.CreateText("PriceLevel", 10),
		StagingColumn.CreateTyped("DiscountPct", EValueType.Decimal5x2),
		StagingColumn.CreateTyped("ValidFrom", EValueType.Date),
		StagingColumn.CreateTyped("ValidTo", EValueType.Date)
	});

	public static readonly StagingTable MoveInOut = new StagingTable("MoveInOut", new[]
	{
		StagingColumn.CreateText("EventId", 20),
		StagingColumn.CreateText("EventType", 10),
		StagingColumn.CreateText("CustomerId", 20),
		StagingColumn.CreateText("AccountId", 20),
		StagingColumn.CreateText("PremiseId", 20),
		StagingColumn.CreateTyped("EventDate", EValueType.Date)
	});

	public static readonly StagingTable Payment = new StagingTable("Payment", new[]
	{
		StagingColumn.CreateText("PaymentId", 20),
		StagingColumn.CreateText("AccountId", 20),
		StagingColumn.CreateTyped("Amount", EValueType.Decimal18x2),
		StagingColumn.CreateText("Currency", 3),
		StagingColumn.CreateTyped("PaymentDate", EValueType.Date),
		StagingColumn.CreateText("PaymentMethod", 10),
		StagingColumn.CreateText("Reference", 35)
	});

	/// <summary>All staging tables, in the order they are written.</summary>
	public static readonly IReadOnlyList<StagingTable> TABLES = new[]
	{
		MessageHeader, Customer, CustomerAddress, Account, Premise, Installation, Meter, MeterRegister,
		Contract, Agreement, MoveInOut, Payment
	};

	/// <summary>All body segments (the header is handled separately by the parser).</summary>
	public static readonly IReadOnlyList<SegmentMap> SEGMENTS = CreateSegments();

	private static IReadOnlyList<SegmentMap> CreateSegments()
	{
		string[] customerTypes = { "CUSTOMER_MASTER" };
		string[] premiseTypes = { "PREMISE_MASTER" };
		string[] meterSwapTypes = { "METER_SWAP" };
		string[] moveTypes = { "MOVE_IN", "MOVE_OUT" };
		string[] paymentTypes = { "PAYMENT_BATCH" };

		FieldMap[] meterFields =
		{
			FieldMap.CreateRequired("MeterId"),
			FieldMap.CreateRequiredFromParent("InstallationId"),
			FieldMap.CreateRequired("SerialNumber"),
			FieldMap.CreateRequired("MeterType"),
			FieldMap.CreateOptional("Manufacturer"),
			FieldMap.CreateRequired("InstallDate"),
			FieldMap.CreateOptional("RemoveDate")
		};

		FieldMap[] registerFields =
		{
			FieldMap.CreateRequiredFromParent("MeterId"),
			FieldMap.CreateRequired("RegisterNo"),
			FieldMap.CreateRequired("RegisterType"),
			FieldMap.CreateRequired("UnitOfMeasure"),
			FieldMap.CreateRequired("Decimals")
		};

		return new[]
		{
			new SegmentMap("/SapMessage/Body/Customer", "Customer", "CustomerId", Customer, new[]
			{
				FieldMap.CreateRequired("CustomerId"),
				FieldMap.CreateRequired("Category"),
				FieldMap.CreateOptional("FirstName"),
				FieldMap.CreateOptional("LastName"),
				FieldMap.CreateOptional("OrgName"),
				FieldMap.CreateOptional("Email"),
				FieldMap.CreateOptional("Phone"),
				FieldMap.CreateRequired("ValidFrom")
			}, customerTypes),
			new SegmentMap("/SapMessage/Body/Customer/Address", "Address", "CustomerId", CustomerAddress, new[]
			{
				FieldMap.CreateRequiredFromParent("CustomerId"),
				FieldMap.CreateRequired("AddressType"),
				FieldMap.CreateRequired("Street"),
				FieldMap.CreateOptional("HouseNo"),
				FieldMap.CreateRequired("PostalCode"),
				FieldMap.CreateRequired("City"),
				FieldMap.CreateRequired("Country")
			}, customerTypes),
			new SegmentMap("/SapMessage/Body/Customer/Account", "Account", "AccountId", Account, new[]
			{
				FieldMap.CreateRequired("AccountId"),
				FieldMap.CreateRequiredFromParent("CustomerId"),
				FieldMap.CreateRequired("AccountClass"),
				FieldMap.CreateRequired("BillingCycle"),
				FieldMap.CreateRequired("PaymentMethod"),
				FieldMap.CreateOptional("CreditLimit"),
				FieldMap.CreateRequired("OpenedOn")
			}, customerTypes),
			new SegmentMap("/SapMessage/Body/Premise", "Premise", "PremiseId", Premise, new[]
			{
				FieldMap.CreateRequired("PremiseId"),
				FieldMap.CreateRequired("ConnectionObjectId"),
				FieldMap.CreateRequired("PremiseType"),
				FieldMap.CreateRequired("Street"),
				FieldMap.CreateOptional("HouseNo"),
				FieldMap.CreateOptional("Unit"),
				FieldMap.CreateRequired("PostalCode"),
				FieldMap.CreateRequired("City"),
				FieldMap.CreateRequired("Country")
			}, premiseTypes),
			new SegmentMap("/SapMessage/Body/Premise/Installation", "Installation", "InstallationId", Installation, new[]
			{
				FieldMap.CreateRequired("InstallationId"),
				FieldMap.CreateRequiredFromParent("PremiseId"),
				FieldMap.CreateRequired("Division"),
				FieldMap.CreateRequired("RateCategory"),
				FieldMap.CreateRequired("ValidFrom")
			}, premiseTypes),
			new SegmentMap("/SapMessage/Body/Premise/Installation/Meter", "Meter", "MeterId", Meter, meterFields, premiseTypes),
			new SegmentMap("/SapMessage/Body/MeterSwap/RemovedMeter", "RemovedMeter", "MeterId", Meter, meterFields, meterSwapTypes),
			new SegmentMap("/SapMessage/Body/MeterSwap/InstalledMeter", "InstalledMeter", "MeterId", Meter, meterFields, meterSwapTypes),
			new SegmentMap("/SapMessage/Body/Premise/Installation/Meter/Register", "Register", "MeterId", MeterRegister, registerFields, premiseTypes),
			new SegmentMap("/SapMessage/Body/MeterSwap/RemovedMeter/Register", "Register", "MeterId", MeterRegister, registerFields, meterSwapTypes),
			new SegmentMap("/SapMessage/Body/MeterSwap/InstalledMeter/Register", "Register", "MeterId", MeterRegister, registerFields, meterSwapTypes),
			new SegmentMap("/SapMessage/Body/Contract", "Contract", "ContractId", Contract, new[]
			{
				FieldMap.CreateRequired("ContractId"),
				FieldMap.CreateRequired("AccountId"),
				FieldMap.CreateRequired("InstallationId"),
				FieldMap.CreateRequired("StartDate"),
				FieldMap.CreateOptional("EndDate")
			}, moveTypes),
			new SegmentMap("/SapMessage/Body/Contract/Agreement", "Agreement", "AgreementId", Agreement, new[]
			{
				FieldMap.CreateRequired("AgreementId"),
				FieldMap.CreateRequiredFromParent("ContractId"),
				FieldMap.CreateRequired("AgreementType"),
				FieldMap.CreateRequired("PriceLevel"),
				FieldMap.CreateOptional("DiscountPct"),
				FieldMap.CreateRequired("ValidFrom"),
				FieldMap.CreateOptional("ValidTo")
			}, moveTypes),
			new SegmentMap("/SapMessage/Body/MoveEvent", "MoveEvent", "EventId", MoveInOut, new[]
			{
				FieldMap.CreateRequired("EventId"),
				FieldMap.CreateRequiredCode("EventType", EVENT_TYPES),
				FieldMap.CreateRequired("CustomerId"),
				FieldMap.CreateRequired("AccountId"),
				FieldMap.CreateRequired("PremiseId"),
				FieldMap.CreateRequired("EventDate")
			}, moveTypes),
			new SegmentMap("/SapMessage/Body/Payment", "Payment", "PaymentId", Payment, new[]
			{
				FieldMap.CreateRequired("PaymentId"),
				FieldMap.CreateRequired("AccountId"),
				FieldMap.CreateRequired("Amount"),
				FieldMap.CreateRequired("Currency"),
				FieldMap.CreateRequired("PaymentDate"),
				FieldMap.CreateRequired("PaymentMethod"),
				FieldMap.CreateOptional("Reference")
			}, paymentTypes)
		};
	}
}
