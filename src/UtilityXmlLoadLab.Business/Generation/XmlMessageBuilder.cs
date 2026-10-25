using System.Globalization;
using System.Text;
using System.Xml;

namespace UtilityXmlLoadLab.Business.Generation;

/// <summary>
/// Writes one IDoc-like message: SapMessage / Header + Body with nested segments.
/// Counts the segments it writes, which is the expected staged row count of a clean message.
/// No XML declaration is written (SQL Server cannot convert an nvarchar with encoding="utf-8").
/// </summary>
public sealed class XmlMessageBuilder : IDisposable
{
	private static readonly XmlWriterSettings WRITER_SETTINGS = CreateWriterSettings();

	public int SegmentCount { get; private set; }

	private readonly StringBuilder _text = new StringBuilder(2048);
	private readonly XmlWriter _writer;

	public XmlMessageBuilder(string messageId, string messageType, DateTime createdAt)
	{
		_writer = XmlWriter.Create(_text, WRITER_SETTINGS);
		_writer.WriteStartElement("SapMessage");
		StartSegment("Header");
		AddLeaf("MessageId", messageId);
		AddLeaf("MessageType", messageType);
		AddLeaf("CreatedAt", createdAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
		AddLeaf("Sender", "ISU-SIM");
		EndElement();
		_writer.WriteStartElement("Body");
	}

	/// <summary>Opens an element that becomes a staging row.</summary>
	public void StartSegment(string name)
	{
		SegmentCount++;
		_writer.WriteStartElement(name);
	}

	/// <summary>Opens a grouping element that is not a row (for example MeterSwap).</summary>
	public void StartGroup(string name)
	{
		_writer.WriteStartElement(name);
	}

	public void EndElement()
	{
		_writer.WriteEndElement();
	}

	/// <summary>Writes a leaf element; nothing is written when the value is null.</summary>
	public void AddLeaf(string name, string? valueOrNull)
	{
		if (valueOrNull != null)
		{
			_writer.WriteElementString(name, valueOrNull);
		}
	}

	public void AddDate(string name, DateTime? dateOrNull)
	{
		AddLeaf(name, dateOrNull?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
	}

	public void AddDecimal(string name, decimal? valueOrNull)
	{
		AddLeaf(name, valueOrNull?.ToString("0.00", CultureInfo.InvariantCulture));
	}

	public string ToXml()
	{
		_writer.WriteEndElement();
		_writer.WriteEndElement();
		_writer.Flush();
		return _text.ToString();
	}

	public void Dispose()
	{
		_writer.Dispose();
	}

	private static XmlWriterSettings CreateWriterSettings()
	{
		XmlWriterSettings settings = new XmlWriterSettings();
		settings.OmitXmlDeclaration = true;
		settings.Indent = false;
		settings.ConformanceLevel = ConformanceLevel.Document;
		return settings;
	}
}
