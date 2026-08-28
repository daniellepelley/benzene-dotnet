using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Benzene.Core.MessageHandlers;
using Benzene.Test.Examples;
using Xunit;

namespace Benzene.Test.Core.Core.MessageHandling;

/// <summary>
/// <c>MessageHandlersList</c> had no direct unit test of its own before this - round 15's finding
/// #227. <see cref="MessageHandlerDefinitionIndexTest"/> only ever exercises it as a single-threaded
/// data source. These pin the two things #227's fix added: <c>Add</c>/<c>FindDefinitions</c> stay
/// correct under concurrent access (the scenario <c>MessageHandlerDefinitionIndex</c>'s own remarks
/// document as supported), and <c>Version</c> still increments once per <c>Add</c>.
/// </summary>
public class MessageHandlersListTest
{
    private static MessageHandlerDefinition Definition(string topic)
        => MessageHandlerDefinition.CreateInstance(topic, typeof(ExampleRequestPayload), typeof(ExampleResponsePayload), typeof(ExampleMessageHandler));

    [Fact]
    public void FindDefinitions_EmptyList_ReturnsEmptyArray()
    {
        var list = new MessageHandlersList();

        Assert.Empty(list.FindDefinitions());
    }

    [Fact]
    public void Add_AppendsAndIsVisibleToFindDefinitions()
    {
        var list = new MessageHandlersList();

        list.Add(Definition("topic-a"));
        list.Add(Definition("topic-b"));

        var definitions = list.FindDefinitions();

        Assert.Equal(2, definitions.Length);
        Assert.Equal("topic-a", definitions[0].Topic.Id);
        Assert.Equal("topic-b", definitions[1].Topic.Id);
    }

    [Fact]
    public void Add_IncrementsVersionOnceEachCall()
    {
        var list = new MessageHandlersList();

        Assert.Equal(0, list.Version);

        list.Add(Definition("topic-a"));
        Assert.Equal(1, list.Version);

        list.Add(Definition("topic-b"));
        Assert.Equal(2, list.Version);
    }

    [Fact]
    public void FindDefinitions_ReturnsASnapshot_NotALiveView()
    {
        var list = new MessageHandlersList();
        list.Add(Definition("topic-a"));

        var snapshot = list.FindDefinitions();
        list.Add(Definition("topic-b"));

        // The array handed back by an earlier call is unaffected by a later Add - FindDefinitions
        // copies (ToArray()) rather than exposing the backing store.
        Assert.Single(snapshot);
    }

    /// <summary>
    /// #227: <c>MessageHandlerDefinitionIndex</c>'s own remarks document a definition being added at
    /// runtime, after the index (and therefore other readers of this list) is already live, as a
    /// supported scenario - that is what its version-stamp invalidation exists for. This drives many
    /// concurrent <see cref="MessageHandlersList.Add"/> calls against many concurrent
    /// <see cref="MessageHandlersList.FindDefinitions"/> reads: neither may throw (no torn
    /// <c>List&lt;T&gt;</c> enumeration/resize race), and once every writer has finished, every
    /// addition must be present exactly once.
    /// </summary>
    [Fact]
    public async Task Add_RacingFindDefinitions_NeitherThrowsAndEveryAdditionSurvives()
    {
        const int writerCount = 8;
        const int perWriterAdds = 200;
        var list = new MessageHandlersList();

        using var readersStop = new System.Threading.CancellationTokenSource();

        var readerExceptions = new System.Collections.Concurrent.ConcurrentBag<System.Exception>();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!readersStop.IsCancellationRequested)
                {
                    // The read itself is the assertion: it must never throw (a torn read on the
                    // backing List<T> would surface as an IndexOutOfRangeException or an
                    // InvalidOperationException from a concurrent resize), and its length must never
                    // exceed what any Add call could have produced so far.
                    var snapshot = list.FindDefinitions();
                    Assert.True(snapshot.Length <= writerCount * perWriterAdds);
                }
            }
            catch (System.Exception ex)
            {
                readerExceptions.Add(ex);
            }
        })).ToArray();

        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriterAdds; i++)
            {
                list.Add(Definition($"topic-{w}-{i}"));
            }
        })).ToArray();

        await Task.WhenAll(writers);
        readersStop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(readerExceptions);

        var finalDefinitions = list.FindDefinitions();
        Assert.Equal(writerCount * perWriterAdds, finalDefinitions.Length);
        Assert.Equal(writerCount * perWriterAdds, list.Version);

        var distinctTopicIds = new HashSet<string>(finalDefinitions.Select(d => d.Topic.Id));
        Assert.Equal(writerCount * perWriterAdds, distinctTopicIds.Count);
    }
}
