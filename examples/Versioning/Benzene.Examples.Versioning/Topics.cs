namespace Benzene.Examples.Versioning;

/// <summary>Topic ids and payload-version names used across the example.</summary>
public static class Topics
{
    public const string OrderCreate = "order:create";
    public const string InventoryAdjust = "inventory:adjust";
}

/// <summary>
/// The payload schema version names. They are ordinary strings on the wire (the <c>benzene-version</c>
/// header); the framework compares them ordinally when picking a handler, so "v1" &lt; "v2" &lt; "v3".
/// </summary>
public static class Versions
{
    public const string V1 = "v1";
    public const string V2 = "v2";
    public const string V3 = "v3";
}
