using Benzene.Schema.OpenApi;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

public class SpecCommand : CommandBase<SpecPayload>
{
    public SpecCommand()
        : base("spec", "Get schemas from a Benzene service")
    { }
    public override async Task ExecuteAsync(SpecPayload payload)
    {
        var source = SpecSourceResolver.Resolve(payload.File, payload.Url, payload.Mesh, payload.Service,
            payload.LambdaName, payload.Profile);
        try
        {
            var json = await source.GetSpecJsonAsync(new SpecRequest(payload.Type, payload.Format));
            Console.WriteLine(json);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }
}

