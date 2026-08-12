namespace Benzene.CodeGen.Core;

public class CodeFileWriter : ICodeFileWriter
{
    public Task CreateAsync(ICodeFile[] codeFiles, string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        return Task.WhenAll(codeFiles.Select(codeFile =>
        {
            var path = Path.Combine(directoryPath, codeFile.Name);

            // A code file's Name can carry its own subdirectory (e.g. topic-client mode's
            // "{Client}/{File}.cs" per-client folders) - create it before writing, or File.WriteAllLinesAsync
            // throws DirectoryNotFoundException for any name that isn't flat under directoryPath.
            var fileDirectory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(fileDirectory) && !Directory.Exists(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            return File.WriteAllLinesAsync(path, codeFile.Lines);
        }));
    }
}
