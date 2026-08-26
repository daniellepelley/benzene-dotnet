using System;
using System.Collections.Generic;
using System.Threading;
using Benzene.Abstractions.Results;
using Benzene.Results;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Idempotency;
using Xunit;

namespace Benzene.Test.Idempotency;

public class IdempotencyMiddlewareTest
{
    private class TestContext : IHasMessageResult
    {
        public IBenzeneResult MessageResult { get; set; } = null!;
    }

    private class FixedKeyStrategy : IIdempotencyKeyStrategy<TestContext>
    {
        private readonly string? _key;
        public FixedKeyStrategy(string? key) => _key = key;
        public string? GetKey(TestContext context) => _key;
    }

    private static IdempotencyMiddleware<TestContext> Middleware(
        IIdempotencyStore store, string? key = "key-1", IdempotencyOptions? options = null,
        ICancellationTokenAccessor? cancellation = null)
        => new(store, new FixedKeyStrategy(key), options ?? new IdempotencyOptions(), cancellation: cancellation);

    // Records the CancellationToken every call was actually invoked with, so a test can assert the
    // ambient token reached the store rather than CancellationToken.None being passed regardless
    // (mirrors the round-8 probe technique used for the sibling ClaimCheck middleware, WP-7 #1).
    private class RecordingIdempotencyStore : IIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();

        public List<CancellationToken> ObservedTokens { get; } = new();

        public Task<ClaimResult> TryClaimAsync(string key, CancellationToken cancellationToken = default)
        {
            ObservedTokens.Add(cancellationToken);
            return _inner.TryClaimAsync(key, cancellationToken);
        }

        public Task<bool> CompleteAsync(string key, string claimToken, bool wasSuccessful, CancellationToken cancellationToken = default)
        {
            ObservedTokens.Add(cancellationToken);
            return _inner.CompleteAsync(key, claimToken, wasSuccessful, cancellationToken);
        }

        public Task<bool> ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken = default)
        {
            ObservedTokens.Add(cancellationToken);
            return _inner.ReleaseAsync(key, claimToken, cancellationToken);
        }
    }

    // #62: the middleware must thread the ambient ICancellationTokenAccessor token into every
    // IIdempotencyStore call (TryClaimAsync, CompleteAsync, ReleaseAsync) rather than silently
    // defaulting to CancellationToken.None - the same gap already fixed for the sibling
    // ClaimCheckHydrateMiddleware/ClaimCheckOffloadMiddleware. A genuinely cancellable seeded token
    // (CanBeCanceled == true) must be observed on every call.
    [Fact]
    public async Task StoreCalls_ObserveTheAmbientCancellationToken_OnTheHappyPath()
    {
        using var cts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var store = new RecordingIdempotencyStore();

        // TryClaimAsync + CompleteAsync.
        await Middleware(store, cancellation: accessor).HandleAsync(new TestContext(), () => Task.CompletedTask);

        Assert.NotEmpty(store.ObservedTokens);
        Assert.All(store.ObservedTokens, token =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.Equal(cts.Token, token);
        });
    }

    [Fact]
    public async Task StoreCalls_ObserveTheAmbientCancellationToken_OnTheReleasePath()
    {
        using var cts = new CancellationTokenSource();
        var accessor = new CancellationTokenAccessor { CancellationToken = cts.Token };
        var store = new RecordingIdempotencyStore();

        // TryClaimAsync + (handler throws) ReleaseAsync.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Middleware(store, cancellation: accessor).HandleAsync(new TestContext(), () =>
                throw new InvalidOperationException("boom")));

        Assert.NotEmpty(store.ObservedTokens);
        Assert.All(store.ObservedTokens, token =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.Equal(cts.Token, token);
        });
    }

    [Fact]
    public async Task FirstMessage_InvokesHandler_AndRecordsCompletion()
    {
        var store = new InMemoryIdempotencyStore();
        var calls = 0;

        await Middleware(store).HandleAsync(new TestContext(), () => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        var claim = await store.TryClaimAsync("key-1");
        Assert.Equal(IdempotencyStatus.Completed, claim.ExistingRecord!.Status);
    }

    [Fact]
    public async Task DuplicateMessage_ShortCircuitsHandler()
    {
        var store = new InMemoryIdempotencyStore();
        var calls = 0;
        Func<Task> next = () => { calls++; return Task.CompletedTask; };

        await Middleware(store).HandleAsync(new TestContext(), next);
        await Middleware(store).HandleAsync(new TestContext(), next);

        Assert.Equal(1, calls); // handler ran only for the first copy
    }

    [Fact]
    public async Task DuplicateOfCompleted_ReplaysSuccessfulResult()
    {
        var store = new InMemoryIdempotencyStore();
        await Middleware(store).HandleAsync(new TestContext(), () => Task.CompletedTask);

        var duplicate = new TestContext();
        await Middleware(store).HandleAsync(duplicate, () => Task.CompletedTask);

        Assert.NotNull(duplicate.MessageResult);
        Assert.True(duplicate.MessageResult.IsSuccessful);
    }

    [Fact]
    public async Task HandlerThrows_ReleasesClaim_SoRedeliveryReprocesses()
    {
        var store = new InMemoryIdempotencyStore();
        var calls = 0;

        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            Middleware(store).HandleAsync(new TestContext(), () =>
            {
                calls++;
                throw new System.InvalidOperationException("boom");
            }));

        // Claim was released: a redelivery gets a fresh claim and reprocesses.
        var reclaim = await store.TryClaimAsync("key-1");
        Assert.True(reclaim.Claimed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HandlerReportsFailureViaResult_ReleasesClaim_SoRedeliveryReprocesses()
    {
        var store = new InMemoryIdempotencyStore();
        var ctx = new TestContext();

        // Handler runs without throwing but the pipeline reports an unsuccessful result.
        await Middleware(store).HandleAsync(ctx, () =>
        {
            ctx.MessageResult = BenzeneResult.UnexpectedError();
            return Task.CompletedTask;
        });

        // The claim was released rather than marked completed, so a redelivery reprocesses.
        Assert.True((await store.TryClaimAsync("key-1")).Claimed);
    }

    [Fact]
    public async Task NoKey_ProcessesNormally_WithoutTouchingStore()
    {
        var store = new InMemoryIdempotencyStore();
        var calls = 0;
        Func<Task> next = () => { calls++; return Task.CompletedTask; };

        await Middleware(store, key: null).HandleAsync(new TestContext(), next);
        await Middleware(store, key: null).HandleAsync(new TestContext(), next);

        Assert.Equal(2, calls); // no de-duplication when there is no key
    }

    [Fact]
    public async Task InProgressDuplicate_WithThrowBehavior_ThrowsConflict()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("key-1"); // simulate a sibling still in progress

        var options = new IdempotencyOptions { InProgressBehavior = InProgressBehavior.Throw };

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            Middleware(store, options: options).HandleAsync(new TestContext(), () => Task.CompletedTask));
    }

    [Fact]
    public async Task InProgressDuplicate_WithSkipBehavior_DropsSilently()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("key-1"); // sibling in progress
        var calls = 0;

        await Middleware(store).HandleAsync(new TestContext(), () => { calls++; return Task.CompletedTask; });

        Assert.Equal(0, calls); // duplicate dropped, handler not invoked
    }
}
