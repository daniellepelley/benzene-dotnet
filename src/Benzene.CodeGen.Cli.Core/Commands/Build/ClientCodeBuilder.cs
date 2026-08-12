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

            // TODO(Phase 3b): scope eventServiceDocument.Requests to a --topics include-list here,
            // one upstream projection before any builder runs - see the plan doc's Phase 3b step 3
            // (work/spec-mesh-tooling-implementation-plan.md). Phase 3's own singular --topic form
            // was superseded by Phase 3b's --topics list and deliberately not implemented.

            payload.ServiceName = ServiceNameResolver.Resolve(payload, eventServiceDocument);

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
