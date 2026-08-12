using System;
using System.Threading.Tasks;
using Benzene.CodeGen.Cli.Core;
using Benzene.CodeGen.Cli.Core.Commands.Build;
using Benzene.CodeGen.Cli.Core.Parsing;
using Xunit;

namespace Benzene.Test.Cli.Builder;

public class CodePayloadMapperTest
{
    [Fact]
    public void Map()
    {
        var input = "command value -profile some-profile -directory some-directory -lambda-name some-lambda-name -output some-output";

        var commandArguments = new CommandParser().Parse(input);

        var codePayload = PayloadMapper.Map<BuildPayload>(commandArguments);

        Assert.Equal("some-lambda-name", codePayload.LambdaName);
        Assert.Equal("some-directory", codePayload.Directory);
        Assert.Equal("some-output", codePayload.Output);
        Assert.Equal("some-profile", codePayload.Profile);
    }
   
    [Fact]
    public void Map_Defaults()
    {
        var input = "command value";

        var commandArguments = new CommandParser().Parse(input);

        var codePayload = PayloadMapper.Map<BuildPayload>(commandArguments);

        Assert.Equal("", codePayload.LambdaName);
        Assert.Equal("", codePayload.Directory);
        Assert.Equal("client", codePayload.Output);
        Assert.Equal("", codePayload.Profile);
    }

    
    [Fact]
    public async Task Map_ConsoleApplication()
    {
        // A bogus profile/lambda name can't reach a real service. Before Phase 2 this passed only
        // because ClientCodeBuilder/AwsLambdaSpecClient swallowed the resulting failure and the CLI
        // exited 0 regardless; now that CLI commands are fail-loud, wiring the arguments through
        // ConsoleApplication end-to-end is expected to throw instead of silently doing nothing.
        var input = "build -profile developer@darwindevelopment -lambda-name \"benzene-main-core-func\" -directory \"C:/Users/Daniel.Le.Pelley/source/repos/Benzene/Output\" -output some-output";

        await Assert.ThrowsAnyAsync<Exception>(() => new ConsoleApplication().ExecuteAsync(input));
    }

}

