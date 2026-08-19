using Benzene.AspNet.Core;
using Benzene.Example.Cloudflare;

// The whole entry point. BenzeneWebHost is the shorthand for the embedded ASP.NET triangle - see its
// docs for the three explicit calls it composes, and for when UseAspNet is the better shape.
await BenzeneWebHost.RunAsync<StartUp>(args);
