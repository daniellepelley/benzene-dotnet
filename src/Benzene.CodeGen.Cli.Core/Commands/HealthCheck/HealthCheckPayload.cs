using Benzene.CodeGen.Cli.Core.Parsing;

namespace Benzene.CodeGen.Cli.Core.Commands.HealthCheck;

public class HealthCheckPayload
{

    [Arg(Name = Constants.Profile, Description = Constants.ProfileDescription)]
    public string Profile { get; set; }
    [Arg(Name = Constants.LambdaName, Description = Constants.LambdaNameDescription)]
    public string LambdaName { get; set; }

    [Arg(Name = Constants.FailOn, DefaultValue = Constants.HealthCheckFailOnDefault, Description = Constants.HealthCheckFailOnDescription)]
    public string FailOn { get; set; }
}
