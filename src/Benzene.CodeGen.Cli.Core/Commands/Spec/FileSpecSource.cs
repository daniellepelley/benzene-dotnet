using Benzene.Schema.OpenApi;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

/// <summary>
/// Reads a spec document straight off disk - typically Phase 1's build-time
/// <c>{Service}.spec.json</c> artifact (<c>Benzene.Descriptor</c>). Fully offline: no network, no
/// AWS SDK call, no deployed service required.
/// </summary>
public class FileSpecSource : ISpecSource
{
    private readonly string _path;

    public FileSpecSource(string path)
    {
        _path = path;
    }

    public async Task<string> GetSpecJsonAsync(SpecRequest request)
    {
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException($"--file '{_path}' does not exist", _path);
        }

        return await File.ReadAllTextAsync(_path);
    }
}
