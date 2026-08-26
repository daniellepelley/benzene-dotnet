using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Aws.Lambda.Kinesis;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Test.Examples;
using Xunit;

namespace Benzene.Test.Aws.Kinesis;

public class KinesisStreamApplicationTest
{
    private static KinesisEventRecord NewRecord(string sequenceNumber)
    {
        return new KinesisEventRecord
        {
            EventSource = "aws:kinesis",
            EventId = "shardId-000000000000:" + sequenceNumber,
            Kinesis = new KinesisRecordData { SequenceNumber = sequenceNumber },
        };
    }

    private static KinesisEvent CreateKinesisEvent(params string[] sequenceNumbers)
    {
        var records = new List<KinesisEventRecord>();
        foreach (var sequenceNumber in sequenceNumbers)
        {
            records.Add(NewRecord(sequenceNumber));
        }
        return new KinesisEvent { Records = records };
    }

    private static KinesisStreamApplication BuildApplication(
        Func<StreamContext<KinesisEventRecord>, Task> process, KinesisStreamOptions options = null)
    {
        var services = ServiceResolverMother.CreateServiceCollection();
        var pipeline = new MiddlewarePipelineBuilder<StreamContext<KinesisEventRecord>>(
                new MicrosoftBenzeneServiceContainer(services))
            .UseStream(process)
            .Build();

        return new KinesisStreamApplication(pipeline, options);
    }

    private static IServiceResolverFactory ServiceResolverFactory()
    {
        return ServiceResolverMother.CreateServiceResolverFactory();
    }

    [Fact]
    public async Task HandleAsync_AllRecordsCheckpointed_ReturnsEmptyBatchItemFailures()
    {
        var application = BuildApplication(async context =>
        {
            await foreach (var record in context.Items)
            {
                await context.Checkpointer.CheckpointAsync(record);
            }
        });

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2", "3"), ServiceResolverFactory());

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task HandleAsync_CheckpointingAForeignRecord_DoesNotRewindTheResumePoint()
    {
        // A handler that checkpoints record 2 (real), then a record that isn't in the batch by
        // reference (e.g. a projected/transformed copy), must NOT have its resume point rewound to the
        // start of the batch - IndexOf returns -1 for the foreign record, and the old code set the
        // watermark to -1 (reprocess everything). The watermark only advances now.
        var application = BuildApplication(async context =>
        {
            var index = 0;
            await foreach (var record in context.Items)
            {
                index++;
                if (index == 2)
                {
                    await context.Checkpointer.CheckpointAsync(record);                 // real record 2
                    await context.Checkpointer.CheckpointAsync(NewRecord("not-in-batch")); // foreign copy
                    throw new InvalidOperationException("stop after checkpointing");
                }
            }
        });

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2", "3", "4"), ServiceResolverFactory());

        // Resume from record 3 (after the real checkpoint), not record 1 (a rewind to the start).
        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal("3", failure.ItemIdentifier);
    }

    [Fact]
    public async Task HandleAsync_ThrowsAfterCheckpointingRecordTwoOfFive_ReturnsRecordThreesSequenceNumber()
    {
        var application = BuildApplication(async context =>
        {
            var processed = 0;
            await foreach (var record in context.Items)
            {
                processed++;
                if (processed == 3)
                {
                    throw new InvalidOperationException("boom");
                }
                await context.Checkpointer.CheckpointAsync(record);
            }
        });

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2", "3", "4", "5"), ServiceResolverFactory());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal("3", failure.ItemIdentifier);
    }

    [Fact]
    public async Task HandleAsync_ThrowsBeforeCheckpointingAnything_ReturnsFirstRecordsSequenceNumber()
    {
        var application = BuildApplication(_ => throw new InvalidOperationException("boom"));

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2"), ServiceResolverFactory());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal("1", failure.ItemIdentifier);
    }

    [Fact]
    public async Task HandleAsync_SucceedsWithoutCheckpointing_AutoCheckpointOnSuccessDefault_ReturnsEmpty()
    {
        // The UseStream callback overload never checkpoints. A batch that runs to completion without
        // throwing must advance its resume point (default AutoCheckpointOnSuccess) so AWS treats it as
        // done, rather than redelivering the whole batch forever.
        var application = BuildApplication(async context =>
        {
            await foreach (var _ in context.Items) { }
        });

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2", "3"), ServiceResolverFactory());

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task HandleAsync_SucceedsWithoutCheckpointing_AutoCheckpointDisabled_ReturnsFirstRecord()
    {
        // With AutoCheckpointOnSuccess off, a successful batch that checkpointed nothing itself leaves
        // the resume point at the start (full manual control) - the pre-auto-checkpoint behavior.
        var application = BuildApplication(async context =>
        {
            await foreach (var _ in context.Items) { }
        }, new KinesisStreamOptions { AutoCheckpointOnSuccess = false });

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2", "3"), ServiceResolverFactory());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal("1", failure.ItemIdentifier);
    }

    [Fact]
    public async Task HandleAsync_EmptyBatch_ReturnsEmptyBatchItemFailures()
    {
        var application = BuildApplication(_ => Task.CompletedTask);

        var response = await application.HandleAsync(new KinesisEvent { Records = null }, ServiceResolverFactory());

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public void KinesisStreamOptions_Defaults_CatchesExceptions_AndAutoCheckpointsOnSuccess()
    {
        var options = new KinesisStreamOptions();
        Assert.True(options.CatchExceptions);
        Assert.True(options.AutoCheckpointOnSuccess);
    }

    [Fact]
    public async Task HandleAsync_DefaultOptions_PipelineThrows_ExceptionIsCaught_ReturnsPartialResumePoint()
    {
        var application = BuildApplication(_ => throw new InvalidOperationException("boom"));

        var response = await application.HandleAsync(CreateKinesisEvent("1", "2"), ServiceResolverFactory());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal("1", failure.ItemIdentifier);
    }

    [Fact]
    public async Task HandleAsync_CatchExceptionsFalse_PipelineThrows_ExceptionCascades()
    {
        var application = BuildApplication(
            _ => throw new InvalidOperationException("boom"),
            new KinesisStreamOptions { CatchExceptions = false });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.HandleAsync(CreateKinesisEvent("1", "2"), ServiceResolverFactory()));
    }

    [Fact]
    public async Task HandleAsync_ResumePointRecordHasNoKinesisData_DoesNotThrow()
    {
        // Regression test for #162: FirstUncheckpointedSequenceNumber runs from the resultMapper,
        // which the base MiddlewareApplication invokes AFTER CatchAndCheckpointPipeline's own
        // try/catch has already returned - so it used to NRE straight past CatchExceptions whenever
        // the record at the resume point had no Kinesis payload (a malformed record). It must
        // degrade to a null resume point instead of crashing the whole invocation.
        var malformedRecord = new KinesisEventRecord { EventSource = "aws:kinesis", EventId = "shardId-1", Kinesis = null };
        var services = ServiceResolverMother.CreateServiceCollection();
        var pipeline = new MiddlewarePipelineBuilder<StreamContext<KinesisEventRecord>>(
                new MicrosoftBenzeneServiceContainer(services))
            .UseStream(async context =>
            {
                var processed = 0;
                await foreach (var record in context.Items)
                {
                    processed++;
                    if (processed == 1)
                    {
                        await context.Checkpointer.CheckpointAsync(record);
                    }
                    else
                    {
                        throw new InvalidOperationException("boom");
                    }
                }
            })
            .Build();
        var application = new KinesisStreamApplication(pipeline);

        var @event = new KinesisEvent { Records = new List<KinesisEventRecord> { NewRecord("1"), malformedRecord } };

        var response = await application.HandleAsync(@event, ServiceResolverFactory());

        // Can't name a sequence number for a record with no Kinesis payload, so this degrades to "no
        // failure reported" rather than crashing the invocation with an unhandled NRE.
        Assert.Empty(response.BatchItemFailures);
    }
}
