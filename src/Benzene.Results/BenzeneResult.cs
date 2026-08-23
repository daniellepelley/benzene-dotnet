using Benzene.Abstractions.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Results;

public static class BenzeneResult
{
    public static IBenzeneResult Set(string status)
    {
        return Set(status, new Void());
    }

    public static IBenzeneResult<T> Set<T>(string status, bool isSuccessful)
    {
        return ServiceBenzeneResultInternal<T>.Internal(status, isSuccessful);
    }

    /// <summary>
    /// Builds a result with an explicit status and payload, deriving <c>IsSuccessful</c> from the
    /// status class (see core-concepts.md: "Derived from status class") — a known failure status
    /// yields an unsuccessful result even when a payload is supplied.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="status"/> is not one of the framework's known statuses
    /// (<see cref="BenzeneResultStatus.IsKnown"/>). Automatic derivation only makes sense for a
    /// status the framework classifies itself; for an application-defined status, silently guessing
    /// success/failure from an unrecognized string is exactly the kind of silent misclassification
    /// this exercise exists to catch — use <see cref="Set{T}(string, T, bool)"/> instead so the
    /// success/failure of a custom status is explicit, not inferred.
    /// </exception>
    public static IBenzeneResult<T> Set<T>(string status, T payload)
    {
        if (!BenzeneResultStatus.IsKnown(status))
        {
            throw new ArgumentException(
                $"Set(status, payload) can only derive IsSuccessful automatically for one of the " +
                $"framework's known statuses (see BenzeneResultStatus). '{status}' is an " +
                $"application-defined status - use Set(status, payload, isSuccessful) instead so the " +
                $"success/failure of a custom status is explicit, not inferred.",
                nameof(status));
        }

        return ServiceBenzeneResultInternal<T>.Internal(status, payload);
    }

    /// <summary>
    /// Builds a result with an explicit status, payload, and success flag — for the cases where
    /// the success class shouldn't be derived from the status. Example: a health check reports
    /// <c>ServiceUnavailable</c> (so HTTP probes see a 503) but stays successful so the response
    /// body renders the health report payload rather than an error payload.
    /// </summary>
    public static IBenzeneResult<T> Set<T>(string status, T payload, bool isSuccessful)
    {
        return ServiceBenzeneResultInternal<T>.Internal(status, payload, isSuccessful);
    }

    public static IBenzeneResult Set(string status, params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.Internal(status, errors);
    }

    /// <summary>
    /// Builds a failed result carrying the given structured errors under <paramref name="status"/> -
    /// the structured counterpart of <see cref="Set(string, string[])"/>, for producers (validation
    /// integrations, application code) that know the field and/or machine-readable code behind each
    /// error, not just its message.
    /// </summary>
    public static IBenzeneResult Set(string status, IReadOnlyList<BenzeneError> errors)
    {
        return ServiceBenzeneResultInternal<Void>.Internal(status, errors);
    }

    /// <summary>
    /// Builds a failed result carrying the given structured errors under <paramref name="status"/>.
    /// The unambiguous <c>IReadOnlyList&lt;BenzeneError&gt;</c> counterpart never collides with
    /// <see cref="Set{T}(string, T)"/> (its argument type can never be mistaken for a payload) - for a
    /// plain <c>string[]</c> of messages, use <see cref="SetFailed{T}"/> instead, which the same trap
    /// once applied to under this name.
    /// </summary>
    public static IBenzeneResult<T> Set<T>(string status, IReadOnlyList<BenzeneError> errors)
    {
        return ServiceBenzeneResultInternal<T>.Internal(status, errors);
    }

    /// <summary>
    /// Builds a failed result under <paramref name="status"/> carrying the given error messages, with
    /// no payload (<c>default(T)</c>). A distinct name from <see cref="Set{T}(string, T)"/> so a
    /// single string argument here can never be mistaken for a payload.
    /// </summary>
    public static IBenzeneResult<T> SetFailed<T>(string status, params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.Internal(status, errors);
    }

    /// <summary>
    /// Builds a failed, non-generic result carrying a deliberately-authored problem document - the
    /// API for a handler that wants to return a rich problem (custom <see cref="ProblemDetails.Type"/>/
    /// <see cref="ProblemDetails.Instance"/>, or an application subclass of <see cref="ProblemDetails"/>
    /// carrying extension members) rather than the one <see cref="ProblemTypes.From"/> would synthesize.
    /// See <see cref="Problem{T}"/> for the coherence rules (status source, error projection).
    /// </summary>
    public static IBenzeneResult Problem(ProblemDetails problem)
    {
        return Problem<Void>(problem);
    }

    /// <summary>
    /// Builds a failed result carrying a deliberately-authored problem document - the typed
    /// counterpart of <see cref="Problem(ProblemDetails)"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IBenzeneResult.Status"/> comes from <paramref name="problem"/>'s
    /// <see cref="ProblemDetails.BenzeneStatus"/> (required - a problem document with no status is
    /// meaningless as a result, since nothing downstream could classify it), <see cref="IBenzeneResult.Errors"/>
    /// is projected from <see cref="ProblemDetails.Errors"/> (an empty list when it's <c>null</c>), and
    /// the result is always unsuccessful - this factory exists to *fail* deliberately with a rich
    /// document attached, never to succeed with one. Retrieve the attached document later with the
    /// <see cref="BenzeneResultExtensions.GetProblem"/> extension (it returns this document verbatim,
    /// not a synthesized one, because the result implements <see cref="IHasProblemDetails"/>).
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="problem"/>'s <see cref="ProblemDetails.BenzeneStatus"/> is <c>null</c> or empty.
    /// </exception>
    public static IBenzeneResult<T> Problem<T>(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (string.IsNullOrEmpty(problem.BenzeneStatus))
        {
            throw new ArgumentException(
                "problem.BenzeneStatus is required to build a BenzeneResult.Problem(...) - a problem " +
                "document with no status can't be classified by anything downstream. Set " +
                "ProblemDetails.BenzeneStatus before passing it here.",
                nameof(problem));
        }

        var errors = problem.Errors is { Length: > 0 }
            ? (IReadOnlyList<BenzeneError>)problem.Errors
            : Array.Empty<BenzeneError>();

        return AttachReceivedProblem<T>(problem.BenzeneStatus, false, errors, problem);
    }

    /// <summary>
    /// Builds a result under an explicit <paramref name="status"/>/<paramref name="isSuccessful"/>
    /// (not derived from <paramref name="problem"/>) carrying <paramref name="errors"/>, with
    /// <paramref name="problem"/> attached so <see cref="BenzeneResultExtensions.GetProblem"/> returns
    /// it verbatim. Internal - reached through <see cref="Problem{T}"/> for a handler-authored problem
    /// (status IS derived from the problem there), and through <c>Benzene.Clients.Common.ClientResultExtensions</c>
    /// for a client that deserialized a problem document off the wire, where the result's status must
    /// stay whatever the envelope/HTTP classification already decided (see that type's remarks) rather
    /// than being re-derived from the received document's <see cref="ProblemDetails.BenzeneStatus"/>,
    /// which could disagree with it for a still-transitioning producer.
    /// </summary>
    internal static IBenzeneResult<T> AttachReceivedProblem<T>(string status, bool isSuccessful, IReadOnlyList<BenzeneError> errors, ProblemDetails? problem)
    {
        return ServiceBenzeneResultInternal<T>.ProblemInternal(status, isSuccessful, errors, problem);
    }

    public static IBenzeneResult Ok()
    {
        return Ok(new Void());
    }

    public static IBenzeneResult<T> Ok<T>()
    {
        return ServiceBenzeneResultInternal<T>.OkInternal(default);
    }
    
    public static IBenzeneResult<T> Ok<T>(T payload)
    {
        return ServiceBenzeneResultInternal<T>.OkInternal(payload);
    }

    public static IBenzeneResult Created()
    {
        return Created(new Void());
    }

    public static IBenzeneResult<T> Created<T>()
    {
        return ServiceBenzeneResultInternal<T>.CreatedInternal(default);
    }

    public static IBenzeneResult<T> Created<T>(T payload)
    {
        return ServiceBenzeneResultInternal<T>.CreatedInternal(payload);
    }

    public static IBenzeneResult Accepted()
    {
        return Accepted(new Void());
    }

    public static IBenzeneResult<T> Accepted<T>()
    {
        return ServiceBenzeneResultInternal<T>.AcceptedInternal(default);
    }

    public static IBenzeneResult<T> Accepted<T>(T payload)
    {
        return ServiceBenzeneResultInternal<T>.AcceptedInternal(payload);
    }

    public static IBenzeneResult Updated()
    {
        return Updated(new Void());
    }

    public static IBenzeneResult<T> Updated<T>()
    {
        return ServiceBenzeneResultInternal<T>.UpdatedInternal(default);
    }

    public static IBenzeneResult<T> Updated<T>(T payload)
    {
        return ServiceBenzeneResultInternal<T>.UpdatedInternal(payload);
    }

    public static IBenzeneResult Deleted()
    {
        return Deleted(new Void());
    }

    public static IBenzeneResult<T> Deleted<T>()
    {
        return ServiceBenzeneResultInternal<T>.DeletedInternal(default);
    }

    public static IBenzeneResult<T> Deleted<T>(T payload)
    {
        return ServiceBenzeneResultInternal<T>.DeletedInternal(payload);
    }

    public static IBenzeneResult Ignored()
    {
        return ServiceBenzeneResultInternal<Void>.IgnoredInternal();
    }

    public static IBenzeneResult<T> Ignored<T>()
    {
        return ServiceBenzeneResultInternal<T>.IgnoredInternal();
    }

    public static IBenzeneResult ValidationError(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.ValidationErrorInternal(errors);
    }

    public static IBenzeneResult<T> ValidationError<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.ValidationErrorInternal(errors);
    }

    /// <summary>
    /// Builds a <see cref="BenzeneResultStatus.ValidationError"/> result carrying the given structured
    /// errors - the structured counterpart of <see cref="ValidationError(string[])"/>, for the
    /// validation integrations (<c>Benzene.FluentValidation</c>, <c>Benzene.DataAnnotations</c>,
    /// <c>Benzene.JsonSchema</c>) that can attach a field and/or code to each failure.
    /// </summary>
    public static IBenzeneResult ValidationError(IReadOnlyList<BenzeneError> errors)
    {
        return ServiceBenzeneResultInternal<Void>.ValidationErrorInternal(errors);
    }

    /// <summary>
    /// Builds a <see cref="BenzeneResultStatus.ValidationError"/> result carrying the given structured
    /// errors - the structured counterpart of <see cref="ValidationError{T}(string[])"/>.
    /// </summary>
    public static IBenzeneResult<T> ValidationError<T>(IReadOnlyList<BenzeneError> errors)
    {
        return ServiceBenzeneResultInternal<T>.ValidationErrorInternal(errors);
    }

    public static IBenzeneResult NotFound(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.NotFoundInternal(errors);
    }

    public static IBenzeneResult<T> NotFound<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.NotFoundInternal(errors);
    }
    public static IBenzeneResult BadRequest(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.BadRequestInternal(errors);
    }

    public static IBenzeneResult<T> BadRequest<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.BadRequestInternal(errors);
    }

    /// <summary>
    /// Builds a <see cref="BenzeneResultStatus.BadRequest"/> result carrying the given structured
    /// errors - the structured counterpart of <see cref="BadRequest(string[])"/>.
    /// </summary>
    public static IBenzeneResult BadRequest(IReadOnlyList<BenzeneError> errors)
    {
        return ServiceBenzeneResultInternal<Void>.BadRequestInternal(errors);
    }

    /// <summary>
    /// Builds a <see cref="BenzeneResultStatus.BadRequest"/> result carrying the given structured
    /// errors - the structured counterpart of <see cref="BadRequest{T}(string[])"/>.
    /// </summary>
    public static IBenzeneResult<T> BadRequest<T>(IReadOnlyList<BenzeneError> errors)
    {
        return ServiceBenzeneResultInternal<T>.BadRequestInternal(errors);
    }

    public static IBenzeneResult Forbidden(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.ForbiddenInternal(errors);
    }

    public static IBenzeneResult<T> Forbidden<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.ForbiddenInternal(errors);
    }

    public static IBenzeneResult ServiceUnavailable(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.ServiceUnavailableInternal(errors);
    }

    public static IBenzeneResult<T> ServiceUnavailable<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.ServiceUnavailableInternal(errors);
    }

    public static IBenzeneResult UnexpectedError(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.UnexpectedErrorInternal(errors);
    }

    public static IBenzeneResult<T> UnexpectedError<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.UnexpectedErrorInternal(errors);
    }


    public static IBenzeneResult NotImplemented(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.NotImplementedInternal(errors);
    }

    public static IBenzeneResult<T> NotImplemented<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.NotImplementedInternal(errors);
    }

    public static IBenzeneResult<T> Conflict<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.ConflictInternal(errors);
    }

    public static IBenzeneResult Conflict(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.ConflictInternal(errors);
    }

    public static IBenzeneResult<T> Unauthorized<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.UnauthorizedInternal(errors);
    }

    public static IBenzeneResult Unauthorized(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.UnauthorizedInternal(errors);
    }

    public static IBenzeneResult<T> TooManyRequests<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.Internal(BenzeneResultStatus.TooManyRequests, errors);
    }

    public static IBenzeneResult TooManyRequests(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.Internal(BenzeneResultStatus.TooManyRequests, errors);
    }

    public static IBenzeneResult<T> Timeout<T>(params string[] errors)
    {
        return ServiceBenzeneResultInternal<T>.Internal(BenzeneResultStatus.Timeout, errors);
    }

    public static IBenzeneResult Timeout(params string[] errors)
    {
        return ServiceBenzeneResultInternal<Void>.Internal(BenzeneResultStatus.Timeout, errors);
    }

    private class ServiceBenzeneResultInternal<T> : IBenzeneResult<T>, IHasProblemDetails
    {
        // Shared across every successful (and error-less) result so that path allocates nothing for
        // Errors - see work/benzene-result-errors-ruling.md §3.2.
        private static readonly IReadOnlyList<BenzeneError> EmptyErrors = Array.Empty<BenzeneError>();

        private ServiceBenzeneResultInternal(string status, bool isSuccessful)
        {
            Status = status;
            IsSuccessful = isSuccessful;
            Errors = EmptyErrors;
        }

        // IsSuccessful is derived from the status class (see core-concepts.md: "Derived from
        // status class"): a known failure status yields an unsuccessful result even when a
        // payload is supplied. Unknown/application-defined statuses keep the historical
        // successful default — the framework doesn't assume an extension status is a failure.
        private ServiceBenzeneResultInternal(string status, T payload)
            : this(status, payload, !BenzeneResultStatus.IsFailure(status))
        {
        }

        private ServiceBenzeneResultInternal(string status, T payload, bool isSuccessful)
            : this(status, isSuccessful)
        {
            Payload = payload;
        }

        private ServiceBenzeneResultInternal(string status, string[] errors)
            : this(status, false)
        {
            Errors = errors is { Length: > 0 }
                ? Array.ConvertAll(errors, static message => new BenzeneError(message))
                : EmptyErrors;
        }

        private ServiceBenzeneResultInternal(string status, IReadOnlyList<BenzeneError> errors)
            : this(status, false)
        {
            Errors = errors is { Count: > 0 } ? errors : EmptyErrors;
        }

        // The Phase 5 problem-carrying constructor: status/isSuccessful are explicit (not derived
        // from Problem), so both BenzeneResult.Problem<T> (status ← problem.BenzeneStatus) and the
        // client's received-document attachment (status ← the already-classified envelope/HTTP
        // status, see AttachReceivedProblem<T>'s remarks) go through the same representation.
        private ServiceBenzeneResultInternal(string status, bool isSuccessful, IReadOnlyList<BenzeneError> errors, ProblemDetails? problem)
            : this(status, isSuccessful)
        {
            Errors = errors is { Count: > 0 } ? errors : EmptyErrors;
            Problem = problem;
        }

        public string Status { get; }
        public bool IsSuccessful { get; }

        /// <summary>See <see cref="IHasProblemDetails.Problem"/>. <c>null</c> unless attached via
        /// <see cref="BenzeneResult.Problem{T}"/> or the client's received-document path.</summary>
        public ProblemDetails? Problem { get; }
        public IReadOnlyList<BenzeneError> Errors { get; }

        public T Payload { get; }

        public object PayloadAsObject => Payload;

        public static IBenzeneResult<T> Internal(string status, bool isSuccessful)
        {
            return new ServiceBenzeneResultInternal<T>(status, isSuccessful);
        }

        public static IBenzeneResult<T> Internal(string status, T payload)
        {
            return new ServiceBenzeneResultInternal<T>(status, payload);
        }

        public static IBenzeneResult<T> Internal(string status, T payload, bool isSuccessful)
        {
            return new ServiceBenzeneResultInternal<T>(status, payload, isSuccessful);
        }

        public static IBenzeneResult<T> Internal(string status, params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(status, errors);
        }

        public static IBenzeneResult<T> Internal(string status, IReadOnlyList<BenzeneError> errors)
        {
            return new ServiceBenzeneResultInternal<T>(status, errors);
        }

        public static IBenzeneResult<T> ProblemInternal(string status, bool isSuccessful, IReadOnlyList<BenzeneError> errors, ProblemDetails? problem)
        {
            return new ServiceBenzeneResultInternal<T>(status, isSuccessful, errors, problem);
        }

        public static IBenzeneResult<T> OkInternal(T payload)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Ok, payload);
        }

        public static IBenzeneResult<T> CreatedInternal(T payload)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Created, payload);
        }

        public static IBenzeneResult<T> AcceptedInternal(T payload)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Accepted, payload);
        }

        public static IBenzeneResult<T> UpdatedInternal(T payload)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Updated, payload);
        }

        public static IBenzeneResult<T> DeletedInternal(T payload)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Deleted, payload);
        }
        public static IBenzeneResult<T> IgnoredInternal()
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Ignored, true);
        }

        public static IBenzeneResult<T> ValidationErrorInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.ValidationError, errors);
        }

        public static IBenzeneResult<T> ValidationErrorInternal(IReadOnlyList<BenzeneError> errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.ValidationError, errors);
        }

        public static IBenzeneResult<T> NotFoundInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.NotFound, errors);
        }

        public static IBenzeneResult<T> BadRequestInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.BadRequest, errors);
        }

        public static IBenzeneResult<T> BadRequestInternal(IReadOnlyList<BenzeneError> errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.BadRequest, errors);
        }

        public static IBenzeneResult<T> ForbiddenInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Forbidden, errors);
        }

        public static IBenzeneResult<T> ServiceUnavailableInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.ServiceUnavailable, errors);
        }

        public static IBenzeneResult<T> UnexpectedErrorInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.UnexpectedError, errors);
        }

        public static IBenzeneResult<T> NotImplementedInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.NotImplemented, errors);
        }

        public static IBenzeneResult<T> ConflictInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Conflict, errors);
        }

        public static IBenzeneResult<T> UnauthorizedInternal(params string[] errors)
        {
            return new ServiceBenzeneResultInternal<T>(BenzeneResultStatus.Unauthorized, errors);
        }
    }
}
