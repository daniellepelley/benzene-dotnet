using Benzene.CodeGen.Cli.Core.Commands.Spec;
using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;

namespace Benzene.CodeGen.Cli.Core.Commands.Build;

public class ClientCodeBuilder : ICliCodeBuilder
{
    public async Task Build(BuildPayload payload)
    {
        // No try/catch: a failure here (bad profile, unreachable lambda, malformed spec, missing
        // file, unresolvable mesh service, ...) must propagate so the CLI exits non-zero (Phase 2 -
        // a swallowed exception here used to mean `benzene build` always exited 0, even when it
        // produced nothing).
        var source = SpecSourceResolver.Resolve(payload.File, payload.Url, payload.Mesh, payload.Service,
            payload.LambdaName, payload.Profile);
        try
        {
            var json = await source.GetSpecJsonAsync(new SpecRequest("benzene", "json"));

            var eventServiceDocument = new EventServiceDocumentDeserializer().Deserialize(json);

            payload.ServiceName = ServiceNameResolver.Resolve(payload, eventServiceDocument);

            // The --topics include-list (Phase 3b step 3, superseding Phase 3 step 4's unimplemented
            // singular --topic) is applied as one upstream projection of Requests before methods,
            // interface or RequiredTopics are built from it - see CodeBuilderFactory, which threads
            // payload.Topics into ClientSdkOptions, and Benzene.CodeGen.Client.TopicScope, which both
            // client builders apply as the very first step of BuildCodeFiles below.

            var messageClientSdkBuilder = new CodeBuilderFactory().Create(payload);
            var codeFiles = messageClientSdkBuilder.BuildCodeFiles(eventServiceDocument);
            Console.WriteLine("{0} code files created", codeFiles.Length);

            var writer = new CodeFileWriter();

            var directory = string.IsNullOrEmpty(payload.Directory)
                ? Directory.GetCurrentDirectory()
                : payload.Directory;

            await writer.CreateAsync(codeFiles, directory);
            Console.WriteLine("Completed");
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }

}
