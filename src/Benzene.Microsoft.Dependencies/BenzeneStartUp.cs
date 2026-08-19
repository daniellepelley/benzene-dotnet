using Benzene.Abstractions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Microsoft.Dependencies;

/// <summary>
/// Platform-neutral application definition. Derive once; run on any Benzene host
/// (AwsLambdaHost&lt;TStartUp&gt;, IHostBuilder.UseBenzene&lt;TStartUp&gt;()).
/// </summary>
public abstract class BenzeneStartUp : IStartUp<IServiceCollection, IConfiguration, IBenzeneApplicationBuilder>
{
    /// <summary>
    /// Where this service's configuration comes from. Defaults to environment variables, which is
    /// what a container, a Lambda, a Function and a Cloud Run revision all actually inject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Virtual rather than abstract because the overwhelmingly common answer was being written out
    /// by hand in every service - 23 of the 50 StartUps in this repo had this exact body, character
    /// for character, and a new service had to write it before it could compile at all. A steer
    /// should cost a line when you want something different, not a line when you want the default.
    /// </para>
    /// <para>
    /// Override it whenever the default is not what you want - appsettings.json, a base path, a
    /// key vault, a shared <c>DependenciesBuilder.GetConfiguration()</c>. Nothing about the explicit
    /// form has changed; it is one <c>override</c> away, and 27 StartUps here still use it.
    /// </para>
    /// </remarks>
    public virtual IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();
    public abstract void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    public abstract void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration);
}
