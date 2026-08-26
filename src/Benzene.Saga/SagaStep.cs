using Benzene.Abstractions.Results;
using Benzene.Results;

namespace Benzene.Saga;

/// <summary>
/// A saga step whose forward action produces a <typeparamref name="T"/> result, with an optional
/// compensation that undoes it using that result.
/// </summary>
/// <typeparam name="T">The type of the forward action's payload.</typeparam>
/// <remarks>
/// Immutable once constructed: it holds only the forward/compensation delegates and the optional
/// context key, never a per-execution outcome (see <see cref="ISagaStep"/> and the "immutable and
/// concurrency-safe" contract on <see cref="Saga"/>). Every run's state/result/exception lives in the
/// <see cref="SagaStepOutcome"/> returned by <see cref="ExecuteAsync"/>/<see cref="CompensateAsync"/>,
/// not on this instance - so one <see cref="SagaStep{T}"/> instance can be part of multiple concurrent
/// saga runs at once without one run's outcome leaking into another's.
/// </remarks>
public class SagaStep<T> : ISagaStep
{
    private readonly Func<SagaContext, Task<IBenzeneResult<T>>> _forward;
    private readonly Func<SagaContext, T?, Task<IBenzeneResult>>? _compensate;
    private readonly string? _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="SagaStep{T}"/> class.
    /// </summary>
    /// <param name="forward">The forward action, run with the saga context.</param>
    /// <param name="compensate">
    /// The optional compensation, given the context and the forward result; omit for a step with no
    /// effect to undo. The forward result's payload is passed as-is, including <c>null</c> should the
    /// succeeded forward action's <see cref="IBenzeneResult{T}.Payload"/> itself be <c>null</c>.
    /// </param>
    /// <param name="key">An optional explicit context key to publish the result under.</param>
    public SagaStep(Func<SagaContext, Task<IBenzeneResult<T>>> forward,
        Func<SagaContext, T?, Task<IBenzeneResult>>? compensate = null, string? key = null)
    {
        _forward = forward;
        _compensate = compensate;
        _key = key;
    }

    /// <inheritdoc />
    public async Task<SagaStepOutcome> ExecuteAsync(SagaContext context)
    {
        try
        {
            var result = await _forward(context);
            var state = result.IsSuccessful ? SagaStepState.Succeeded : SagaStepState.Failed;
            return new SagaStepOutcome(this, state, result, null);
        }
        catch (Exception ex)
        {
            var result = BenzeneResult.Set<T>(BenzeneResultStatus.UnexpectedError, false);
            return new SagaStepOutcome(this, SagaStepState.Failed, result, ex);
        }
    }

    /// <inheritdoc />
    public void Publish(SagaContext context, SagaStepOutcome outcome)
    {
        if (outcome.State == SagaStepState.Succeeded && outcome.Result is IBenzeneResult<T> typed)
        {
            context.Set(typed.Payload, _key);
        }
    }

    /// <inheritdoc />
    public async Task<SagaStepOutcome> CompensateAsync(SagaContext context, SagaStepOutcome outcome)
    {
        // Only a step that actually succeeded created an effect worth undoing.
        if (outcome.State != SagaStepState.Succeeded)
        {
            return outcome;
        }

        // A succeeded step with no compensation is treated as "nothing to undo" - author a
        // compensation for any step that creates a side effect.
        if (_compensate == null)
        {
            return outcome.WithState(SagaStepState.RolledBack);
        }

        try
        {
            var payload = ((IBenzeneResult<T>)outcome.Result!).Payload;
            var compensationResult = await _compensate(context, payload);
            if (compensationResult.IsSuccessful)
            {
                return outcome.WithState(SagaStepState.RolledBack);
            }
        }
        catch (Exception ex)
        {
            return outcome.WithState(SagaStepState.CompensationFailed, ex);
        }

        return outcome.WithState(SagaStepState.CompensationFailed);
    }
}
