using System.Xml;
using Benzene.Core.Exceptions;

namespace Benzene.Xml;

/// <summary>
/// A transparent <see cref="XmlReader"/> decorator guarding <see cref="XmlSerializer.Deserialize(Type, string)"/>
/// against unbounded recursion (#260, mirroring <c>Benzene.Avro</c>'s <c>BoundedBinaryDecoder</c> shape
/// for the pull-based <see cref="XmlReader"/> API instead of Avro's <c>Decoder</c>). Every member
/// delegates unchanged to the wrapped reader except <see cref="Read"/>, which additionally checks the
/// wrapped reader's own <see cref="Depth"/> - already correctly tracked by the BCL for every node shape
/// (empty/self-closing elements, attributes, text, CDATA, comments, ...) - against a configured maximum
/// whenever the current node is an element start, throwing <see cref="BenzeneException"/> once exceeded.
/// <see cref="System.Xml.Serialization.XmlSerializer"/>'s internally-generated deserializer recurses one
/// CLR call stack frame deeper for every nested element it reads (a self-referencing or very
/// deeply-nested request DTO - a comment tree, category tree, org chart - drives that recursion
/// unboundedly); throwing here, from inside one of those recursive <see cref="Read"/> calls, unwinds
/// that recursion via the exception well before it can blow the stack, instead of letting it run
/// unbounded into an uncatchable <see cref="StackOverflowException"/>.
/// </summary>
internal sealed class DepthGuardedXmlReader : XmlReader, IXmlLineInfo
{
    private readonly XmlReader _inner;
    private readonly IXmlLineInfo? _innerLineInfo;
    private readonly int _maxDepth;

    /// <summary>Initializes a new instance wrapping <paramref name="inner"/>.</summary>
    /// <param name="inner">The reader to wrap and guard.</param>
    /// <param name="maxDepth">The maximum element-nesting depth to allow before throwing.</param>
    public DepthGuardedXmlReader(XmlReader inner, int maxDepth)
    {
        _inner = inner;
        _maxDepth = maxDepth;
        // XmlReader.Create's own reader implements IXmlLineInfo, which the generated deserializer
        // uses (when present) to annotate its own exception messages with a line/position - forward
        // it so wrapping this reader doesn't degrade those messages.
        _innerLineInfo = inner as IXmlLineInfo;
    }

    /// <inheritdoc />
    public bool HasLineInfo() => _innerLineInfo?.HasLineInfo() ?? false;

    /// <inheritdoc />
    public int LineNumber => _innerLineInfo?.LineNumber ?? 0;

    /// <inheritdoc />
    public int LinePosition => _innerLineInfo?.LinePosition ?? 0;

    /// <inheritdoc />
    /// <exception cref="BenzeneException">The wrapped reader's element depth exceeds the configured maximum.</exception>
    public override bool Read()
    {
        var result = _inner.Read();

        if (result && _inner.NodeType == XmlNodeType.Element && _inner.Depth > _maxDepth)
        {
            throw new BenzeneException(
                $"XML payload exceeded the maximum nesting depth of {_maxDepth} while deserializing " +
                $"(reached depth {_inner.Depth}). This guards against unbounded recursion - e.g. a " +
                "self-referencing or very deeply-nested request DTO - driving an uncatchable CLR stack " +
                "overflow. Increase XmlOptions.MaxDepth if this is a legitimate deeply-nested payload.");
        }

        return result;
    }

    // Everything below is a plain, unmodified forward to the wrapped reader - this decorator's only
    // behavior change is the guard in Read() above.
    public override int AttributeCount => _inner.AttributeCount;

    public override string BaseURI => _inner.BaseURI;

    public override int Depth => _inner.Depth;

    public override bool EOF => _inner.EOF;

    public override string GetAttribute(int i) => _inner.GetAttribute(i);

    public override string? GetAttribute(string name) => _inner.GetAttribute(name);

    public override string? GetAttribute(string name, string? namespaceURI) => _inner.GetAttribute(name, namespaceURI);

    public override bool HasValue => _inner.HasValue;

    public override bool IsEmptyElement => _inner.IsEmptyElement;

    public override string this[int i] => _inner[i];

    public override string? this[string name] => _inner[name];

    public override string? this[string name, string? namespaceURI] => _inner[name, namespaceURI];

    public override string LocalName => _inner.LocalName;

    public override string? LookupNamespace(string prefix) => _inner.LookupNamespace(prefix);

    public override bool MoveToAttribute(string name) => _inner.MoveToAttribute(name);

    public override bool MoveToAttribute(string name, string? ns) => _inner.MoveToAttribute(name, ns);

    public override bool MoveToElement() => _inner.MoveToElement();

    public override bool MoveToFirstAttribute() => _inner.MoveToFirstAttribute();

    public override bool MoveToNextAttribute() => _inner.MoveToNextAttribute();

    public override XmlNameTable NameTable => _inner.NameTable;

    public override string NamespaceURI => _inner.NamespaceURI;

    public override XmlNodeType NodeType => _inner.NodeType;

    public override string Prefix => _inner.Prefix;

    public override bool ReadAttributeValue() => _inner.ReadAttributeValue();

    public override ReadState ReadState => _inner.ReadState;

    public override void ResolveEntity() => _inner.ResolveEntity();

    public override string Value => _inner.Value;

    public override void Close() => _inner.Dispose();
}
