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

    /// <summary>
    /// An explicit <c>--namespace</c> override for 'build', or null/empty to keep
    /// <see cref="CodeBuilderFactory"/>'s original <c>--lambda-name</c>/<see cref="ServiceName"/>-based
    /// derivation unchanged. When given, it is used exactly (no magic suffix) as the generated
    /// namespace.
    /// </summary>
    string Namespace { get; }

    /// <summary>
    /// A comma-delimited <c>--topics</c> include-list for 'build', or null/empty for "every
    /// non-reserved topic" (see <see cref="Benzene.CodeGen.Client.ClientSdkOptions.Topics"/>).
    /// </summary>
    string Topics { get; }
}
