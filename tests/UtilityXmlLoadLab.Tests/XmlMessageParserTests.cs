using UtilityXmlLoadLab.Business.Parsing;
using UtilityXmlLoadLab.Core.Mapping;
using UtilityXmlLoadLab.Core.Messages;

namespace UtilityXmlLoadLab.Tests;

public class XmlMessageParserTests
{
	private const string HEADER = "<Header><MessageId>M1</MessageId><MessageType>PAYMENT_BATCH</MessageType>"
		+ "<CreatedAt>2024-01-05T10:11:12</CreatedAt><Sender>SIM</Sender></Header>";

	private const string PAYMENT = "<Payment><PaymentId>P1</PaymentId><AccountId>A1</AccountId><Amount>12.50</Amount>"
		+ "<Currency>USD</Currency><PaymentDate>2024-01-05</PaymentDate><PaymentMethod>DD</PaymentMethod></Payment>";

	private readonly XmlMessageParser _parser = new XmlMessageParser();

	[Fact]
	public void ParseMessage_ValidMessage_PassesWithTypedRows()
	{
		MessageResult result = Parse("<SapMessage>" + HEADER + "<Body>" + PAYMENT + PAYMENT + "</Body></SapMessage>");

		Assert.Equal(EValidationStatus.Passed, result.Status);
		Assert.Equal(3, result.ExpectedRowCount);
		Assert.Equal(3, result.Rows.Count);

		StagedRow payment = result.Rows.First(row => row.Table == StagingMap.Payment);
		Assert.Equal(12.50M, payment.Values[2]);
		Assert.Equal(new DateTime(2024, 1, 5), payment.Values[4]);
	}

	[Fact]
	public void ParseMessage_Malformed_FailsWithoutRows()
	{
		MessageResult result = Parse("<SapMessage>" + HEADER + "<Body>" + PAYMENT);

		Assert.Equal(EValidationStatus.Failed, result.Status);
		Assert.Null(result.ExpectedRowCount);
		Assert.Empty(result.Rows);
		Assert.StartsWith("Malformed XML", result.ErrorDetail);
	}

	[Fact]
	public void ParseMessage_BadValue_FailsAllOrNothing()
	{
		string badPayment = PAYMENT.Replace("12.50", "12,50");
		MessageResult result = Parse("<SapMessage>" + HEADER + "<Body>" + PAYMENT + badPayment + "</Body></SapMessage>");

		Assert.Equal(EValidationStatus.Failed, result.Status);
		Assert.Empty(result.Rows);
		Assert.Equal("Payment P1: Amount '12,50' is not a valid decimal", result.ErrorDetail);
	}

	[Fact]
	public void ParseMessage_SegmentInUnexpectedPlace_FailsInsteadOfDropping()
	{
		MessageResult result = Parse("<SapMessage>" + HEADER + "<Body><Wrapper>" + PAYMENT + "</Wrapper></Body></SapMessage>");

		Assert.Equal(EValidationStatus.Failed, result.Status);
		Assert.Equal(2, result.ExpectedRowCount);
		Assert.Contains("Found 2 segments but mapped 1", result.ErrorDetail);
	}

	[Fact]
	public void ParseMessage_SegmentInWrongMessageType_Fails()
	{
		string header = HEADER.Replace("PAYMENT_BATCH", "MOVE_IN");
		MessageResult result = Parse("<SapMessage>" + header + "<Body>" + PAYMENT + "</Body></SapMessage>");

		Assert.Equal(EValidationStatus.Failed, result.Status);
		Assert.Contains("Found 2 segments but mapped 1", result.ErrorDetail);
	}

	[Fact]
	public void ParseMessage_HeaderMessageIdMismatch_Fails()
	{
		MessageResult result = _parser.ParseMessage(new RawMessage("OTHER", DateTime.UtcNow,
			"<SapMessage>" + HEADER + "<Body>" + PAYMENT + "</Body></SapMessage>"));

		Assert.Equal(EValidationStatus.Failed, result.Status);
		Assert.Contains("Header: MessageId does not match the Inbox MessageId", result.ErrorDetail);
	}

	[Fact]
	public void ParseMessage_ParentKey_ComesFromEnclosingSegment()
	{
		string header = HEADER.Replace("PAYMENT_BATCH", "CUSTOMER_MASTER");
		string body = "<Customer><CustomerId>C1</CustomerId><Category>PERSON</Category><ValidFrom>2020-01-01</ValidFrom>"
			+ "<Account><AccountId>A1</AccountId><AccountClass>RES</AccountClass><BillingCycle>MONTHLY</BillingCycle>"
			+ "<PaymentMethod>DD</PaymentMethod><OpenedOn>2020-01-01</OpenedOn></Account></Customer>";

		MessageResult result = Parse("<SapMessage>" + header + "<Body>" + body + "</Body></SapMessage>");

		Assert.Equal(EValidationStatus.Passed, result.Status);
		StagedRow account = result.Rows.Single(row => row.Table == StagingMap.Account);
		Assert.Equal("C1", account.Values[1]);
	}

	[Fact]
	public void ParseMessage_HashMatchesSqlServerHashBytesOfNvarchar()
	{
		// HASHBYTES('SHA2_256', N'abc') in SQL Server = SHA-256 of the UTF-16LE bytes.
		MessageResult result = _parser.ParseMessage(new RawMessage("M1", DateTime.UtcNow, "abc"));

		Assert.Equal(32, result.RawXmlHash.Length);
		Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes("abc"))),
			Convert.ToHexString(result.RawXmlHash));
	}

	private MessageResult Parse(string xml)
	{
		return _parser.ParseMessage(new RawMessage("M1", DateTime.UtcNow, xml));
	}
}
