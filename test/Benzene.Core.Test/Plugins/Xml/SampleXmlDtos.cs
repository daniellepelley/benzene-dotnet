namespace Benzene.Test.Plugins.Xml;

// Self-referencing DTO used by the XML depth-guard regression tests (#260): mirrors Benzene.Avro's
// `Node` (test/Benzene.Core.Test/Plugins/Avro/SampleDtos.cs) - a chain of these exercises the same
// unbounded-recursion shape (a comment tree / category tree / org chart) the ruling identified, without
// needing an actual cyclic object graph. Named XmlChainNode (not XmlNode) to avoid any confusion with
// the unrelated BCL System.Xml.XmlNode.
public class XmlChainNode
{
    public string Name { get; set; } = string.Empty;
    public XmlChainNode? Next { get; set; }
}
