using Benzene.CodeGen.Cli.Core.Parsing;

namespace Benzene.CodeGen.Cli.Core.Commands.Build;

public class BuildPayload : ICommandPayload
{
    [Arg(Name = Constants.Profile, Description = Constants.ProfileDescription)]
    public string Profile { get; set; }
    [Arg(Name = Constants.LambdaName, Description = Constants.LambdaNameDescription)]
    public string LambdaName { get; set; }
    [Arg(Name = Constants.File, Description = Constants.FileDescription)]
    public string File { get; set; }
    [Arg(Name = Constants.Url, Description = Constants.UrlDescription)]
    public string Url { get; set; }
    [Arg(Name = Constants.Mesh, Description = Constants.MeshDescription)]
    public string Mesh { get; set; }
    [Arg(Name = Constants.Service, Description = Constants.ServiceDescription)]
    public string Service { get; set; }
    [Arg(Name = Constants.ServiceName, Description = Constants.ServiceNameDescription)]
    public string ServiceName { get; set; }
    [Arg(Name = Constants.Output, DefaultValue = Constants.OutputDefault, Description = Constants.OutputDescription)]
    public string Output { get; set; }
    [Arg(Name = Constants.Directory, Description = Constants.DirectoryDescription)]
    public string Directory { get; set; }
    [Arg(Name = Constants.Namespace, Description = Constants.NamespaceDescription)]
    public string Namespace { get; set; }
    [Arg(Name = Constants.Topics, Description = Constants.TopicsDescription)]
    public string Topics { get; set; }
}

