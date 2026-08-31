using Benzene.Core.Versioning.CasterBuilder;
using Xunit;

namespace Benzene.Test.Core.Versioning;

// Regression coverage for #226: CasterFuncBuilder.CreateCasterFunc used to memoize a compiled caster
// delegate only *after* Expression.Lambda(...).Compile() returned. Building the mapping expression
// for a self-referential or mutually-recursive versioned DTO shape re-enters CreateCasterFunc for the
// same (fromType, toType) pair before anything was memoized, so the guard never tripped and the
// recursion was unbounded - an uncatchable StackOverflowException (process exit code 134) reachable
// through the documented CasterFactory/Upcast API on the most ordinary of DTO shapes (a tree, a
// linked list, two types referencing each other).
//
// A StackOverflowException cannot be caught in-proc, so the crash itself was verified separately
// (before the fix landed) by running an equivalent probe in a child `dotnet` process and observing
// exit code 134 - see the [RESOLVED] #226 entry in work/outstanding-bugs.md for that evidence. These
// tests assert the fixed, in-proc behaviour: building and using a recursive caster graph completes
// normally and produces correct output.
public class CasterRecursionTest
{
    // Self-referential: NodeFrom.Child : NodeFrom -> NodeTo.Child : NodeTo. Mapping the Child property
    // requires a caster for the exact same (NodeFrom, NodeTo) pair the outer CreateCasterFunc call is
    // already building.
    private class NodeFrom
    {
        public string Name { get; set; }
        public NodeFrom Child { get; set; }
    }

    private class NodeTo
    {
        public string Name { get; set; }
        public NodeTo Child { get; set; }
    }

    [Fact]
    public void Build_SelfReferentialType_DoesNotRecurseUnbounded()
    {
        // Previously an uncatchable StackOverflowException before Build() could ever return.
        var caster = new CasterFactory<NodeFrom, NodeTo>().Build();

        Assert.NotNull(caster);
    }

    [Fact]
    public void Cast_SelfReferentialType_MapsNullChildToNull()
    {
        var caster = new CasterFactory<NodeFrom, NodeTo>().Build();

        var result = caster.Cast(new NodeFrom { Name = "root", Child = null });

        Assert.Equal("root", result.Name);
        Assert.Null(result.Child);
    }

    [Fact]
    public void Cast_SelfReferentialType_MapsMultiLevelTree()
    {
        var caster = new CasterFactory<NodeFrom, NodeTo>().Build();

        var from = new NodeFrom
        {
            Name = "root",
            Child = new NodeFrom
            {
                Name = "child",
                Child = new NodeFrom
                {
                    Name = "grandchild",
                    Child = null
                }
            }
        };

        var result = caster.Cast(from);

        Assert.Equal("root", result.Name);
        Assert.Equal("child", result.Child.Name);
        Assert.Equal("grandchild", result.Child.Child.Name);
        Assert.Null(result.Child.Child.Child);
    }

    // Mutually recursive: AFrom.Other : BFrom, BFrom.Other : AFrom -> ATo.Other : BTo, BTo.Other : ATo.
    // Building the (AFrom, ATo) caster requires a (BFrom, BTo) caster, which in turn requires the very
    // (AFrom, ATo) caster that is still being built.
    private class AFrom
    {
        public string Name { get; set; }
        public BFrom Other { get; set; }
    }

    private class BFrom
    {
        public string Name { get; set; }
        public AFrom Other { get; set; }
    }

    private class ATo
    {
        public string Name { get; set; }
        public BTo Other { get; set; }
    }

    private class BTo
    {
        public string Name { get; set; }
        public ATo Other { get; set; }
    }

    [Fact]
    public void Build_MutuallyRecursiveTypes_DoesNotRecurseUnbounded()
    {
        var caster = new CasterFactory<AFrom, ATo>().Build();

        Assert.NotNull(caster);
    }

    [Fact]
    public void Cast_MutuallyRecursiveTypes_MapsNullOtherToNull()
    {
        var caster = new CasterFactory<AFrom, ATo>().Build();

        var result = caster.Cast(new AFrom { Name = "a", Other = null });

        Assert.Equal("a", result.Name);
        Assert.Null(result.Other);
    }

    [Fact]
    public void Cast_MutuallyRecursiveTypes_MapsMultiLevelChain()
    {
        var caster = new CasterFactory<AFrom, ATo>().Build();

        var from = new AFrom
        {
            Name = "a1",
            Other = new BFrom
            {
                Name = "b1",
                Other = new AFrom
                {
                    Name = "a2",
                    Other = null
                }
            }
        };

        var result = caster.Cast(from);

        Assert.Equal("a1", result.Name);
        Assert.Equal("b1", result.Other.Name);
        Assert.Equal("a2", result.Other.Other.Name);
        Assert.Null(result.Other.Other.Other);
    }

    // The reverse direction (B as the root type) exercises the mutual pair starting from the other
    // side, so both (AFrom,ATo) and (BFrom,BTo) get a chance to be the *first* pair built (and
    // therefore the one that must install the indirection cell the other one resolves through).
    [Fact]
    public void Build_MutuallyRecursiveTypes_FromEitherSide_DoesNotRecurseUnbounded()
    {
        var caster = new CasterFactory<BFrom, BTo>().Build();

        var result = caster.Cast(new BFrom { Name = "b", Other = new AFrom { Name = "a", Other = null } });

        Assert.Equal("b", result.Name);
        Assert.Equal("a", result.Other.Name);
        Assert.Null(result.Other.Other);
    }
}
