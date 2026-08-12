namespace Benzene.CodeGen.Cli.Core.Commands.Build;

public interface ICommandPayload
{
    string LambdaName { get; }
    string Output { get; }

    /// <summary>
    /// The resolved service name to derive the generated service name/namespace from, or
    /// null/empty to keep deriving from <see cref="LambdaName"/> (the original, --lambda-name-only
    /// behavior). <see cref="ClientCodeBuilder"/> resolves this (explicit <c>--service-name</c>, or
    /// the source-specific default) before <see cref="CodeBuilderFactory"/> reads it.
    /// </summary>
    string ServiceName { get; }
}
