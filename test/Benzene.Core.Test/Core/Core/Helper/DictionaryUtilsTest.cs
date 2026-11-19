using System;
using System.Collections.Generic;
using Benzene.Core.Helper;
using Xunit;

namespace Benzene.Test.Core.Core.Helper;

public class DictionaryUtilsTest
{
    private class Enrichable
    {
        public string Name { get; set; }
        public int Age { get; set; }
        public string Untouched { get; set; } = "default";
    }

    private enum Colour
    {
        Red,
        Green,
        Blue
    }

    // FR-1: collection properties bind every value of a repeated query key.
    private class CollectionEnrichable
    {
        public string[] Ids { get; set; }
        public List<int> Numbers { get; set; }
        public IReadOnlyList<Colour> Colours { get; set; }
        public IEnumerable<Guid> Guids { get; set; }
        public ICollection<int?> Optional { get; set; }
        public IList<string> Names { get; set; }
        public IReadOnlyCollection<string> Tags { get; set; }
        public HashSet<string> Unsupported { get; set; }
        public string Scalar { get; set; }
        public int Count { get; set; }
        public List<string> FromBody { get; set; } = new() { "body" };
    }

    private class TypedEnrichable
    {
        public Guid Id { get; set; }
        public Guid? OptionalId { get; set; }
        public int? Age { get; set; }
        public Colour Colour { get; set; }
    }

    [Fact]
    public void MapOnto_ExistingKeyWithDefaultValue_IsReplaced()
    {
        var source = new Dictionary<string, object>
        {
            { "foo", null }
        };
        var overlay = new Dictionary<string, object>
        {
            { "foo", "bar" }
        };

        var result = DictionaryUtils.MapOnto(source, overlay);

        Assert.Equal("bar", result["foo"]);
    }

    [Fact]
    public void MapOnto_ExistingKeyWithNonDefaultValue_IsNotReplaced()
    {
        var source = new Dictionary<string, object>
        {
            { "foo", "original" }
        };
        var overlay = new Dictionary<string, object>
        {
            { "foo", "bar" }
        };

        var result = DictionaryUtils.MapOnto(source, overlay);

        Assert.Equal("original", result["foo"]);
    }

    [Fact]
    public void MapOnto_StringOverload_MapsOntoSource()
    {
        var source = new Dictionary<string, object>();
        var overlay = new Dictionary<string, string>
        {
            { "foo", "bar" }
        };

        DictionaryUtils.MapOnto(source, overlay);

        Assert.Equal("bar", source["foo"]);
    }

    [Fact]
    public void MapOnto_LowercasesKeys()
    {
        var source = new Dictionary<string, object>();
        var overlay = new Dictionary<string, object> { { "FOO", "bar" } };

        var result = DictionaryUtils.MapOnto(source, overlay);

        Assert.True(result.ContainsKey("foo"));
        Assert.False(result.ContainsKey("FOO"));
    }

    [Fact]
    public void MapOnto_NullOverlay_ReturnsSourceUnchanged()
    {
        var source = new Dictionary<string, object> { { "foo", "bar" } };

        var result = DictionaryUtils.MapOnto(source, (IDictionary<string, object>)null);

        Assert.Equal("bar", result["foo"]);
    }

    [Fact]
    public void Combine_UsesFirstOccurrenceOfEachKey()
    {
        var first = new Dictionary<string, string> { { "a", "1" }, { "b", "2" } };
        var second = new Dictionary<string, string> { { "b", "should-not-win" }, { "c", "3" } };

        var result = DictionaryUtils.Combine(new[] { first, second });

        Assert.Equal("1", result["a"]);
        Assert.Equal("2", result["b"]);
        Assert.Equal("3", result["c"]);
    }

    [Fact]
    public void FilterAndReplace_RenamesKeysAndDropsUnfiltered()
    {
        var source = new Dictionary<string, string>
        {
            { "oldKey", "value1" },
            { "unfiltered", "value2" }
        };
        var filter = new Dictionary<string, string>
        {
            { "oldkey", "newKey" }
        };

        var result = DictionaryUtils.FilterAndReplace(source, filter);

        Assert.Single(result);
        Assert.Equal("value1", result["newKey"]);
    }

    [Fact]
    public void KeyEquals_MatchingValue_ReturnsTrue()
    {
        var dictionary = new Dictionary<string, string> { { "key", "value" } };

        Assert.True(DictionaryUtils.KeyEquals(dictionary, "key", "value"));
    }

    [Fact]
    public void KeyEquals_NonMatchingValue_ReturnsFalse()
    {
        var dictionary = new Dictionary<string, string> { { "key", "other" } };

        Assert.False(DictionaryUtils.KeyEquals(dictionary, "key", "value"));
    }

    [Fact]
    public void KeyEquals_NullDictionary_ReturnsFalse()
    {
        Assert.False(DictionaryUtils.KeyEquals(null, "key", "value"));
    }

    [Fact]
    public void KeyEquals_MissingKey_ReturnsFalse()
    {
        var dictionary = new Dictionary<string, string>();

        Assert.False(DictionaryUtils.KeyEquals(dictionary, "key", "value"));
    }

    [Fact]
    public void Enrich_SetsMatchingPropertiesCaseInsensitively()
    {
        var source = new Enrichable();
        var dictionary = new Dictionary<string, object>
        {
            { "NAME", "foo" },
            { "age", 42 },
            { "unmatched", "ignored" }
        };

        var result = DictionaryUtils.Enrich(source, dictionary);

        Assert.Equal("foo", result.Name);
        Assert.Equal(42, result.Age);
        Assert.Equal("default", result.Untouched);
    }

    [Fact]
    public void Enrich_ConvertsValueTypeWhenItDiffersFromPropertyType()
    {
        var source = new Enrichable();
        // "age" arrives as a string here (e.g. from a route parameter), but the property is an int.
        var dictionary = new Dictionary<string, object> { { "age", "42" } };

        var result = DictionaryUtils.Enrich(source, dictionary);

        Assert.Equal(42, result.Age);
    }

    [Fact]
    public void Enrich_GuidPropertyFromString_Assigns()
    {
        // A route parameter/header arrives as a string; the DTO property is a Guid.
        var id = Guid.NewGuid();
        var result = DictionaryUtils.Enrich(new TypedEnrichable(),
            new Dictionary<string, object> { { "Id", id.ToString() } });

        Assert.Equal(id, result.Id);
    }

    [Fact]
    public void Enrich_NullableGuidPropertyFromString_Assigns()
    {
        var id = Guid.NewGuid();
        var result = DictionaryUtils.Enrich(new TypedEnrichable(),
            new Dictionary<string, object> { { "OptionalId", id.ToString() } });

        Assert.Equal(id, result.OptionalId);
    }

    [Fact]
    public void Enrich_NullableIntPropertyFromString_Assigns()
    {
        var result = DictionaryUtils.Enrich(new TypedEnrichable(),
            new Dictionary<string, object> { { "Age", "42" } });

        Assert.Equal(42, result.Age);
    }

    [Fact]
    public void Enrich_EnumPropertyFromString_Assigns()
    {
        var result = DictionaryUtils.Enrich(new TypedEnrichable(),
            new Dictionary<string, object> { { "Colour", "Green" } });

        Assert.Equal(Colour.Green, result.Colour);
    }

    [Fact]
    public void Enrich_NullSource_CreatesNewInstance()
    {
        var dictionary = new Dictionary<string, object>
        {
            { "Name", "foo" }
        };

        var result = DictionaryUtils.Enrich<Enrichable>(null, dictionary);

        Assert.NotNull(result);
        Assert.Equal("foo", result.Name);
    }

    [Fact]
    public void Enrich_EmptyDictionary_ReturnsSourceUntouched()
    {
        var source = new Enrichable { Name = "original" };

        var result = DictionaryUtils.Enrich(source, new Dictionary<string, object>());

        Assert.Same(source, result);
        Assert.Equal("original", result.Name);
    }

    [Fact]
    public void Enrich_DuplicateCaseInsensitiveKeys_FirstOccurrenceWins()
    {
        // Preserves the .First() semantics: if a caller's dictionary somehow has two differently-cased
        // keys for the same property, the first-enumerated one wins rather than throwing.
        var source = new Enrichable();
        var dictionary = new Dictionary<string, object> { { "Name", "first" }, { "NAME", "second" } };

        var result = DictionaryUtils.Enrich(source, dictionary);

        Assert.Equal("first", result.Name);
    }

    [Fact]
    public void Enrich_RepeatedCallsForSameType_ProduceIndependentCorrectResults()
    {
        // Exercises the compiled-setter cache across multiple distinct instances of the same T.
        var first = DictionaryUtils.Enrich(new Enrichable(), new Dictionary<string, object> { { "Name", "alice" } });
        var second = DictionaryUtils.Enrich(new Enrichable(), new Dictionary<string, object> { { "Name", "bob" } });

        Assert.Equal("alice", first.Name);
        Assert.Equal("bob", second.Name);
    }

    [Fact]
    public void Enrich_ArrayProperty_FromSeveralValues_TakesAllInOrder()
    {
        var result = DictionaryUtils.Enrich(new CollectionEnrichable(),
            new Dictionary<string, object> { { "ids", new[] { "a", "b" } } });

        Assert.Equal(new[] { "a", "b" }, result.Ids);
    }

    [Fact]
    public void Enrich_CollectionProperty_FromOneValue_IsOneElement()
    {
        var result = DictionaryUtils.Enrich(new CollectionEnrichable(),
            new Dictionary<string, object> { { "ids", "a" }, { "numbers", "7" } });

        Assert.Equal(new[] { "a" }, result.Ids);
        Assert.Equal(new List<int> { 7 }, result.Numbers);
    }

    [Fact]
    public void Enrich_CollectionProperties_ConvertEachElement()
    {
        var guid = Guid.NewGuid();
        var result = DictionaryUtils.Enrich(new CollectionEnrichable(), new Dictionary<string, object>
        {
            { "numbers", new[] { "1", "2" } },
            { "colours", new[] { "red", "BLUE" } },
            { "guids", new[] { guid.ToString() } },
            { "optional", new[] { "3", "4" } },
            { "names", new[] { "x", "y" } },
            { "tags", new[] { "t" } }
        });

        Assert.Equal(new List<int> { 1, 2 }, result.Numbers);
        Assert.Equal(new[] { Colour.Red, Colour.Blue }, result.Colours);
        Assert.Equal(new[] { guid }, result.Guids);
        Assert.Equal(new int?[] { 3, 4 }, result.Optional);
        Assert.Equal(new[] { "x", "y" }, result.Names);
        Assert.Equal(new[] { "t" }, result.Tags);
    }

    [Fact]
    public void Enrich_ScalarProperty_FromSeveralValues_TakesTheFirst()
    {
        var result = DictionaryUtils.Enrich(new CollectionEnrichable(), new Dictionary<string, object>
        {
            { "scalar", new[] { "a", "b" } },
            { "count", new[] { "5", "6" } }
        });

        Assert.Equal("a", result.Scalar);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Enrich_StringProperty_IsNotTreatedAsACollection()
    {
        var result = DictionaryUtils.Enrich(new CollectionEnrichable(),
            new Dictionary<string, object> { { "scalar", "abc" } });

        Assert.Equal("abc", result.Scalar);
    }

    [Fact]
    public void Enrich_CollectionProperty_WithNoMatchingKey_KeepsItsValue()
    {
        var result = DictionaryUtils.Enrich(new CollectionEnrichable(),
            new Dictionary<string, object> { { "scalar", "abc" } });

        Assert.Equal(new List<string> { "body" }, result.FromBody);
    }

    [Fact]
    public void Enrich_CollectionProperty_ElementThatDoesNotConvert_Throws()
    {
        Assert.ThrowsAny<FormatException>(() => DictionaryUtils.Enrich(new CollectionEnrichable(),
            new Dictionary<string, object> { { "numbers", new[] { "1", "x" } } }));
    }

    [Fact]
    public void Enrich_UnsupportedCollectionType_ThrowsAsBefore()
    {
        Assert.Throws<InvalidCastException>(() => DictionaryUtils.Enrich(new CollectionEnrichable(),
            new Dictionary<string, object> { { "unsupported", new[] { "a" } } }));
    }
}
