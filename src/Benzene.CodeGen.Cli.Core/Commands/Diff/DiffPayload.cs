using Benzene.CodeGen.Cli.Core.Parsing;

namespace Benzene.CodeGen.Cli.Core.Commands.Diff;

public class DiffPayload
{
    [Arg(Name = Constants.Baseline, Description = Constants.BaselineDescription)]
    public string Baseline { get; set; }

    [Arg(Name = Constants.Current, Description = Constants.CurrentDescription)]
    public string Current { get; set; }

    [Arg(Name = Constants.FailOn, DefaultValue = Constants.FailOnDefault, Description = Constants.FailOnDescription)]
    public string FailOn { get; set; }

    [Arg(Name = Constants.WarnOnly, Description = Constants.WarnOnlyDescription)]
    public string WarnOnly { get; set; }

    [Arg(Name = Constants.Format, DefaultValue = Constants.DiffFormatDefault, Description = Constants.DiffFormatDescription)]
    public string Format { get; set; }
}
