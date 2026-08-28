namespace Benzene.Xml;

/// <summary>
/// Configures <see cref="XmlSerializer"/>'s deserialize path.
/// </summary>
public class XmlOptions
{
    /// <summary>The default for <see cref="MaxDepth"/> when not overridden.</summary>
    public const int DefaultMaxDepth = 32;

    /// <summary>
    /// The maximum XML element-nesting depth <see cref="XmlSerializer.Deserialize(Type, string)"/> will
    /// follow before throwing <see cref="Benzene.Core.Exceptions.BenzeneException"/> (#260). XML is a
    /// negotiable request/response media format, so <c>Deserialize</c> consumes attacker-controlled
    /// request bodies. <see cref="System.Xml.Serialization.XmlSerializer"/>'s generated reader recurses
    /// one CLR call stack frame deeper for every nested element it reads - fine for an ordinary flat/
    /// shallow DTO, but a self-referencing or very deeply-nested request shape (a comment tree, category
    /// tree, org chart - all legitimate, common shapes) drives that recursion arbitrarily deep. Left
    /// unbounded, a deeply-nested body well under any reasonable body-size limit crashes the whole
    /// process with an uncatchable <see cref="System.StackOverflowException"/>. This bounds it well
    /// before that point, mirroring <c>Benzene.Avro</c>'s equivalent guard (<c>AvroOptions.MaxDepth</c>/
    /// <c>BoundedBinaryDecoder</c>) and MessagePack-CSharp's <c>MessagePackSecurity.UntrustedData</c>
    /// depth cap - the same bug class, closed the same way.
    /// Defaults to <see cref="DefaultMaxDepth"/> (32) - comfortably above any reasonable real request
    /// shape but far below the depth that actually crashes the process. Serialization (writing a
    /// response from trusted, in-process data) is not guarded by this option - only <c>Deserialize</c>
    /// reads an untrusted body.
    /// </summary>
    public int MaxDepth { get; set; } = DefaultMaxDepth;
}
