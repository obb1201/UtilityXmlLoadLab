using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using UtilityXmlLoadLab.Core.Mapping;
using UtilityXmlLoadLab.Core.Messages;

namespace UtilityXmlLoadLab.Business.Parsing;

/// <summary>
/// Approach B parser. Reads one message with a forward-only <see cref="XmlReader"/>, maps segments
/// to typed staging rows with <see cref="StagingMap"/>, and validates them with
/// <see cref="FieldValidator"/>. Same checks as usp_ShredBatch, so both approaches agree.
/// All-or-nothing: a message with any problem returns no rows.
/// </summary>
public sealed class XmlMessageParser
{
	private const int MAX_ERROR_DETAIL_LENGTH = 4000;
	private const string MALFORMED_TEXT = "Malformed XML: the message is not well-formed.";

	private static readonly XmlReaderSettings READER_SETTINGS = CreateReaderSettings();
	private static readonly StagingColumn HEADER_MESSAGE_TYPE = StagingMap.MessageHeader.Columns[0];
	private static readonly StagingColumn HEADER_CREATED_AT = StagingMap.MessageHeader.Columns[1];
	private static readonly StagingColumn HEADER_SENDER = StagingMap.MessageHeader.Columns[2];
	private static readonly FieldMap REQUIRED_FIELD = FieldMap.CreateRequired("Header");

	private readonly Dictionary<string, SegmentMap> _segmentsByPath;

	public XmlMessageParser()
	{
		_segmentsByPath = StagingMap.SEGMENTS.ToDictionary(segment => segment.Path, StringComparer.Ordinal);
	}

	public MessageResult ParseMessage(RawMessage message)
	{
		byte[] rawXmlHash = SHA256.HashData(Encoding.Unicode.GetBytes(message.RawXml));

		List<ElementFrame> headerFrames = new List<ElementFrame>();
		List<(ElementFrame Frame, SegmentMap Segment)> segmentFrames = new List<(ElementFrame, SegmentMap)>();
		int expectedRowCount = 0;

		try
		{
			expectedRowCount = ReadFrames(message.RawXml, headerFrames, segmentFrames);
		}
		catch (XmlException exception)
		{
			string detail = MALFORMED_TEXT + " (" + exception.Message + ")";
			return new MessageResult(message.MessageId, message.ReceivedAt, rawXmlHash, null, null,
				EValidationStatus.Failed, GetLimitedText(detail), Array.Empty<StagedRow>());
		}

		List<string> errors = new List<string>();
		List<StagedRow> rows = new List<StagedRow>(headerFrames.Count + segmentFrames.Count);
		string? messageType = null;

		if (headerFrames.Count == 0)
		{
			errors.Add("Header is missing.");
		}
		else if (headerFrames.Count > 1)
		{
			errors.Add("Header appears more than once.");
		}

		foreach (ElementFrame headerFrame in headerFrames)
		{
			StagedRow headerRow = MapHeader(headerFrame, message.MessageId, errors);
			messageType ??= GetMessageTypeForEvidence(headerFrame);
			rows.Add(headerRow);
		}

		// A segment is only mapped for the message types that carry it (same rule as usp_ShredBatch).
		// Otherwise it stays unmapped and the count check below fails the message.
		string? rawMessageType = headerFrames.Count > 0 ? headerFrames[0].GetChildTextOrNull("MessageType") : null;
		foreach ((ElementFrame frame, SegmentMap segment) in segmentFrames.Where(item => rawMessageType != null && item.Segment.MessageTypes.Contains(rawMessageType)))
		{
			rows.Add(MapSegment(frame, segment, errors));
		}

		if (expectedRowCount != rows.Count)
		{
			errors.Add("Found " + expectedRowCount.ToString(CultureInfo.InvariantCulture) + " segments but mapped "
				+ rows.Count.ToString(CultureInfo.InvariantCulture) + "; a segment is in an unexpected place.");
		}

		if (errors.Count > 0)
		{
			return new MessageResult(message.MessageId, message.ReceivedAt, rawXmlHash, messageType, expectedRowCount,
				EValidationStatus.Failed, GetLimitedText(string.Join("; ", errors)), Array.Empty<StagedRow>());
		}

		return new MessageResult(message.MessageId, message.ReceivedAt, rawXmlHash, messageType, expectedRowCount,
			EValidationStatus.Passed, null, rows);
	}

	/// <summary>Streams the XML once. Returns the expected row count (all segment elements, anywhere).</summary>
	private int ReadFrames(string rawXml, List<ElementFrame> headerFrames, List<(ElementFrame, SegmentMap)> segmentFrames)
	{
		int expectedRowCount = 0;
		Stack<ElementFrame> openFrames = new Stack<ElementFrame>();

		using StringReader stringReader = new StringReader(rawXml);
		using XmlReader reader = XmlReader.Create(stringReader, READER_SETTINGS);

		while (reader.Read())
		{
			switch (reader.NodeType)
			{
				case XmlNodeType.Element:
				{
					if (StagingMap.SEGMENT_ELEMENT_NAMES.Contains(reader.LocalName))
					{
						expectedRowCount++;
					}

					ElementFrame? parentOrNull = openFrames.Count > 0 ? openFrames.Peek() : null;

					// Elements in a namespace never match the mapping paths (same as T-SQL).
					string pathPart = reader.NamespaceURI.Length == 0 ? reader.LocalName : "{" + reader.NamespaceURI + "}" + reader.LocalName;
					string path = (parentOrNull?.Path ?? string.Empty) + "/" + pathPart;
					ElementFrame frame = new ElementFrame(reader.LocalName, path, parentOrNull);

					if (reader.IsEmptyElement)
					{
						CloseFrame(frame, headerFrames, segmentFrames);
					}
					else
					{
						openFrames.Push(frame);
					}

					break;
				}

				case XmlNodeType.Text:
				case XmlNodeType.CDATA:
				case XmlNodeType.SignificantWhitespace:
					if (openFrames.Count > 0)
					{
						openFrames.Peek().AddText(reader.Value);
					}

					break;

				case XmlNodeType.EndElement:
					CloseFrame(openFrames.Pop(), headerFrames, segmentFrames);
					break;

				default:
					break;
			}
		}

		return expectedRowCount;
	}

	private void CloseFrame(ElementFrame frame, List<ElementFrame> headerFrames, List<(ElementFrame, SegmentMap)> segmentFrames)
	{
		if (frame.TextOrNull != null && frame.ParentOrNull != null)
		{
			frame.ParentOrNull.AddChildText(frame.Name, frame.TextOrNull);
		}

		if (frame.Path == StagingMap.HEADER_PATH)
		{
			headerFrames.Add(frame);
		}
		else if (_segmentsByPath.TryGetValue(frame.Path, out SegmentMap? segment))
		{
			segmentFrames.Add((frame, segment));
		}
	}

	private static StagedRow MapHeader(ElementFrame headerFrame, string inboxMessageId, List<string> errors)
	{
		string? headerMessageId = headerFrame.GetChildTextOrNull("MessageId");
		if (headerMessageId == null)
		{
			errors.Add("Header: MessageId is missing");
		}
		else if (headerMessageId != inboxMessageId)
		{
			errors.Add("Header: MessageId does not match the Inbox MessageId");
		}

		string? messageType = headerFrame.GetChildTextOrNull("MessageType");
		if (messageType == null)
		{
			errors.Add("Header: MessageType is missing");
		}
		else if (!StagingMap.MESSAGE_TYPES.Contains(messageType))
		{
			errors.Add("Header: MessageType '" + FieldValidator.GetLeft(messageType) + "' is unknown");
		}

		object? createdAt = FieldValidator.ConvertValue(headerFrame.GetChildTextOrNull("CreatedAt"), HEADER_CREATED_AT, REQUIRED_FIELD, out string? createdAtProblem);
		AddProblem(errors, "Header: ", createdAtProblem);

		object? sender = FieldValidator.ConvertValue(headerFrame.GetChildTextOrNull("Sender"), HEADER_SENDER, REQUIRED_FIELD, out string? senderProblem);
		AddProblem(errors, "Header: ", senderProblem);

		return new StagedRow(StagingMap.MessageHeader, new object?[] { messageType, createdAt, sender });
	}

	private static StagedRow MapSegment(ElementFrame frame, SegmentMap segment, List<string> errors)
	{
		StagingTable table = segment.Table;
		string?[] rawValues = new string?[segment.Fields.Count];
		string? keyOrNull = null;

		for (int index = 0; index < segment.Fields.Count; index++)
		{
			FieldMap field = segment.Fields[index];
			rawValues[index] = field.IsFromParent
				? frame.ParentOrNull?.GetChildTextOrNull(field.ElementName)
				: frame.GetChildTextOrNull(field.ElementName);

			if (field.ElementName == segment.KeyElementName)
			{
				keyOrNull = rawValues[index];
			}
		}

		// Same text as T-SQL: "<Label> <key or ?>: <problem>".
		string problemPrefix = segment.Label + " " + (keyOrNull == null ? "?" : FieldValidator.GetLeft(keyOrNull)) + ": ";
		object?[] values = new object?[table.Columns.Count];

		for (int index = 0; index < segment.Fields.Count; index++)
		{
			values[index] = FieldValidator.ConvertValue(rawValues[index], table.Columns[index], segment.Fields[index], out string? problem);
			AddProblem(errors, problemPrefix, problem);
		}

		return new StagedRow(table, values);
	}

	private static string? GetMessageTypeForEvidence(ElementFrame headerFrame)
	{
		string? messageType = headerFrame.GetChildTextOrNull("MessageType");
		if (messageType == null)
		{
			return null;
		}

		return messageType.Length <= HEADER_MESSAGE_TYPE.MaxLength ? messageType : messageType.Substring(0, HEADER_MESSAGE_TYPE.MaxLength);
	}

	private static void AddProblem(List<string> errors, string prefix, string? problemOrNull)
	{
		if (problemOrNull != null)
		{
			errors.Add(prefix + problemOrNull);
		}
	}

	private static string GetLimitedText(string text)
	{
		return text.Length <= MAX_ERROR_DETAIL_LENGTH ? text : text.Substring(0, MAX_ERROR_DETAIL_LENGTH);
	}

	private static XmlReaderSettings CreateReaderSettings()
	{
		XmlReaderSettings settings = new XmlReaderSettings();
		settings.DtdProcessing = DtdProcessing.Prohibit;
		settings.XmlResolver = null;
		settings.IgnoreComments = true;
		settings.IgnoreProcessingInstructions = true;
		settings.IgnoreWhitespace = true;
		settings.CloseInput = false;
		return settings;
	}
}
