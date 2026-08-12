using Benzene.CodeGen.Cli.Core.Parsing;

namespace Benzene.CodeGen.Cli.Core.Commands.Spec;

public class SpecPayload
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
    [Arg(Name = Constants.Type, DefaultValue = Constants.TypeDefault, Description = Constants.TypeDescription)]
    public string Type { get; set; }
    [Arg(Name = Constants.Format, DefaultValue = Constants.FormatDefault, Description = Constants.FormatDescription)]
    public string Format { get; set; }
}

