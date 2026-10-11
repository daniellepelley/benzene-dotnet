using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.Info;
using Benzene.Results;
using Benzene.Core;
using Microsoft.Extensions.Logging;

namespace Benzene.Aws.Lambda.DynamoDb;

/// <summary>
/// Processes a DynamoDB Streams batch by running each record through the middleware pipeline
/// <b>sequentially, in shard order, stopping at the first failure</b> (plan decision DS5) —
/// deliberately unlike <c>SqsApplication</c>'s concurrent fan-out. Stream records are ordered
/// change-data-capture: processing them concurrently, or continuing past a failure, breaks the
/// per-key ordering the stream guarantees. The first failed record's sequence number is reported
/// as the batch item failure, so Lambda checkpoints there and redelivers from that record.
/// </summary>
/// <remarks>
/// By default any failure (thrown, returned, or no result at all) stops the batch at that record and
/// reports it - ordering is the whole point of this shape (see the class summary), and
/// <c>work/archive/batch-failure-handling-2026-07.md</c>'s DS5 note records why there is no
/// "catch and keep going" toggle of the SNS/S3/EventBridge kind. The one exception is
/// <see cref="DynamoDbOptions.AtMostOnce"/>, the at-most-once override spec 0.2 requires of every
/// one-way binding (transport-bindings.md §1, obligation 8): opt in and a failed record is logged and
/// skipped instead, the rest of the batch still running in order.
/// </remarks>
public class DynamoDbApplication : IMiddlewareApplication<DynamoDbEvent, DynamoDbBatchResponse>
{
    private readonly IMiddlewarePipeline<DynamoDbRecordContext> _pipeline;
    private readonly DynamoDbOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbApplication"/> class.
    /// </summary>
    /// <param name="pipeline">The built DynamoDB middleware pipeline to run each record through.</param>
    public DynamoDbApplication(IMiddlewarePipeline<DynamoDbRecordContext> pipeline)
        : this(pipeline, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbApplication"/> class with settlement options.
    /// </summary>
    /// <param name="pipeline">The built DynamoDB middleware pipeline to run each record through.</param>
    /// <param name="options">How the batch settles; <c>null</c> for the default (stop at the first failure).</param>
    public DynamoDbApplication(IMiddlewarePipeline<DynamoDbRecordContext> pipeline, DynamoDbOptions? options)
    {
        _pipeline = new TransportMiddlewarePipeline<DynamoDbRecordContext>(TransportNames.DynamoDb, pipeline);
        _options = options ?? new DynamoDbOptions();
    }

    /// <summary>
    /// Handles a stream batch, running each record through the pipeline in its own service scope,
    /// in order, until a record fails or the batch completes.
    /// </summary>
    /// <param name="event">The DynamoDB Streams batch event to process.</param>
    /// <param name="serviceResolverFactory">The service resolver factory used to create a scope per record.</param>
    /// <returns>
    /// A task that resolves to a batch response. On success the failure list is empty; on the first
    /// failed record (unsuccessful handler result or exception) it contains that record's sequence
    /// number and the remaining records are left unprocessed for redelivery.
    /// </returns>
    public async Task<DynamoDbBatchResponse> HandleAsync(DynamoDbEvent @event, IServiceResolverFactory serviceResolverFactory)
    {
        foreach (var record in @event.Records)
        {
            var context = DynamoDbRecordContext.CreateInstance(@event, record);
            var infrastructureFailure = false;
            try
            {
                using (var scope = serviceResolverFactory.CreateScope())
                {
                    await _pipeline.HandleAsync(context, scope);
                }
            }
            catch (Exception ex)
            {
                infrastructureFailure = BenzeneFailure.IsInfrastructure(ex);
                using (var loggingScope = serviceResolverFactory.CreateScope())
                {
                    loggingScope.GetService<ILogger<DynamoDbApplication>>()
                        .LogError(ex, infrastructureFailure
                    ? BenzeneFailure.InfrastructureLogPrefix + " Processing DynamoDB stream record {sequenceNumber} failed — this service is mis-wired; the message is not at fault"
                    : "Processing DynamoDB stream record {sequenceNumber} failed", record.Dynamodb?.SequenceNumber);
                }

                context.MessageResult = BenzeneResult.UnexpectedError();
            }

            // Treat a null result as failure (not just an explicit false), matching the SQS reference:
            // a record whose pipeline short-circuited without setting a result must NOT be checkpointed
            // past in an ordered CDC stream - that would silently skip it. Stop here for redelivery.
            if (context.MessageResult?.IsSuccessful != true)
            {
                // The at-most-once override (transport-bindings §1, obligation 8) waives redelivery for
                // a record's own failure - logged, then skipped - but not for an infrastructure
                // (mis-wiring) failure, which is no record's outcome and still stops the batch.
                if (_options.AtMostOnce && !infrastructureFailure)
                {
                    using (var loggingScope = serviceResolverFactory.CreateScope())
                    {
                        loggingScope.GetService<ILogger<DynamoDbApplication>>().LogWarning(
                            "DynamoDB stream record {sequenceNumber} failed with {status}; acknowledged without redelivery (AtMostOnce)",
                            record.Dynamodb?.SequenceNumber, context.MessageResult?.Status ?? "<no result>");
                    }

                    continue;
                }

                return new DynamoDbBatchResponse
                {
                    BatchItemFailures = new List<DynamoDbBatchResponse.BatchItemFailure>
                    {
                        new DynamoDbBatchResponse.BatchItemFailure
                        {
                            ItemIdentifier = record.Dynamodb?.SequenceNumber ?? record.EventId
                        }
                    }
                };
            }
        }

        return new DynamoDbBatchResponse();
    }
}
