using System;
using System.IO;
using System.Threading.Tasks;
using Benzene.CodeGen.Core;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Core;

public class CodeFileWriterTest
{
    private class TestCodeFile : ICodeFile
    {
        public string Name { get; set; }
        public string[] Lines { get; set; }
    }

    [Fact]
    public async Task CreateAsync_DirectoryDoesNotExist_CreatesItAndWritesEachFile()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "benzene-codefilewriter-test-" + Guid.NewGuid());
        try
        {
            var codeFiles = new ICodeFile[]
            {
                new TestCodeFile { Name = "Foo.cs", Lines = new[] { "namespace Foo;", "public class Foo { }" } },
                new TestCodeFile { Name = "Bar.cs", Lines = new[] { "namespace Bar;", "public class Bar { }" } }
            };

            await new CodeFileWriter().CreateAsync(codeFiles, directoryPath);

            Assert.True(Directory.Exists(directoryPath));
            Assert.Equal(new[] { "namespace Foo;", "public class Foo { }" }, await File.ReadAllLinesAsync(Path.Combine(directoryPath, "Foo.cs")));
            Assert.Equal(new[] { "namespace Bar;", "public class Bar { }" }, await File.ReadAllLinesAsync(Path.Combine(directoryPath, "Bar.cs")));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateAsync_NestedFileNames_CreatesSubdirectoriesAndWritesEachFile()
    {
        // topic-client mode's per-client "{Client}/{File}.cs" names used to throw
        // DirectoryNotFoundException here - CreateAsync only ever created directoryPath itself, never
        // a code file's own subdirectory.
        var directoryPath = Path.Combine(Path.GetTempPath(), "benzene-codefilewriter-nested-test-" + Guid.NewGuid());
        try
        {
            var codeFiles = new ICodeFile[]
            {
                new TestCodeFile { Name = "UserGet/UserGetServiceClient.cs", Lines = new[] { "namespace UserGet;" } },
                new TestCodeFile { Name = "UserGet/IUserGetServiceClient.cs", Lines = new[] { "namespace UserGet;" } },
                new TestCodeFile { Name = "TenantGet/TenantGetServiceClient.cs", Lines = new[] { "namespace TenantGet;" } },
            };

            await new CodeFileWriter().CreateAsync(codeFiles, directoryPath);

            Assert.True(File.Exists(Path.Combine(directoryPath, "UserGet", "UserGetServiceClient.cs")));
            Assert.True(File.Exists(Path.Combine(directoryPath, "UserGet", "IUserGetServiceClient.cs")));
            Assert.True(File.Exists(Path.Combine(directoryPath, "TenantGet", "TenantGetServiceClient.cs")));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
