using System;
using System.Collections.Generic;

namespace Benzene.Test.Plugins.Avro;

public enum SampleStatus
{
    Pending,
    Filled,
    Cancelled
}

public class SampleOrderDto
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public long Reference { get; set; }
    public decimal Price { get; set; }
    public double Weight { get; set; }
    public bool Active { get; set; }
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public SampleStatus Status { get; set; }
    public List<string> Tags { get; set; } = new();
    public int? OptionalCount { get; set; }
    public SampleLegDto Leg { get; set; } = new();
}

public class SampleLegDto
{
    public string Label { get; set; } = string.Empty;
    public double Amount { get; set; }
}

public class Point
{
    public int X { get; set; }
    public int Y { get; set; }
}

// Self-referencing DTO used by the depth-guard regression tests (#56): a chain of these built deep
// enough exercises the same unbounded-recursion shape as the round-8/9 DoS repro, without actually
// recursing far enough to crash the test process.
public class Node
{
    public string Name { get; set; } = string.Empty;
    public Node? Next { get; set; }
}

// Schema-mismatch regression pair (#57): ThreeFieldDto is the "old producer" shape; TwoFieldDto is
// the same type with the MIDDLE field (B) removed, as if a consumer had upgraded independently.
public class ThreeFieldDto
{
    public string A { get; set; } = string.Empty;
    public string B { get; set; } = string.Empty;
    public string C { get; set; } = string.Empty;
}

public class TwoFieldDto
{
    public string A { get; set; } = string.Empty;
    public string C { get; set; } = string.Empty;
}

// Schema-mismatch regression pair (#57): same field names, declared in a different order - as if a
// consumer had reordered its DTO's properties independently of the producer. B is a plain non-nullable
// int (bare Avro "int", no union wrapper) while A is a nullable int (union-wrapped) - deliberately
// different wire shapes, so a reorder desyncs the byte stream instead of the two fields silently
// swapping values (which is what happens, undetectably, when both fields share the same wire shape -
// not a useful regression repro). With A's value chosen >= 2, the desynced read lands A's own encoded
// int value where a union branch index (0 or 1 only) is expected, throwing IndexOutOfRangeException
// cleanly - deliberately not going through a length-prefixed bytes/string read at all, since that would
// instead (and validly) trip the unrelated #56 depth/size guards rather than this schema-mismatch path.
public class OrderedAbDto
{
    public int? A { get; set; }
    public int B { get; set; }
}

public class OrderedBaDto
{
    public int B { get; set; }
    public int? A { get; set; }
}
