using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Auth.Core.Endpoints;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Test.Examples;

// FR-5: handlers carrying their own authorization requirements, enforced by UseEndpointAuthorization().
public class AuthEchoRequest
{
    public string Name { get; set; }
}

public abstract class AuthEchoHandler : IMessageHandler<AuthEchoRequest, HeaderEchoResponse>
{
    public Task<IBenzeneResult<HeaderEchoResponse>> HandleAsync(AuthEchoRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(new HeaderEchoResponse { Id = GetType().Name }));
    }
}

[Message("auth:read")]
[HttpEndpoint("GET", Defaults.AuthReadPath)]
[RequireScope("tasks.read", "tasks.admin")]
public class AuthReadHandler : AuthEchoHandler
{
}

[Message("auth:write")]
[HttpEndpoint("POST", Defaults.AuthWritePath)]
[RequireScope("tasks.write")]
public class AuthWriteHandler : AuthEchoHandler
{
}

[Message("auth:open")]
[HttpEndpoint("GET", Defaults.AuthOpenPath)]
[AllowAnonymous]
public class AuthOpenHandler : AuthEchoHandler
{
}

[Message("auth:plain")]
[HttpEndpoint("GET", Defaults.AuthPlainPath)]
public class AuthPlainHandler : AuthEchoHandler
{
}

[Message("auth:admin")]
[HttpEndpoint("GET", Defaults.AuthAdminPath)]
[RequireScope("tasks.read")]
[RequireRole("admin")]
public class AuthAdminHandler : AuthEchoHandler
{
}

[Message("auth:policy")]
[HttpEndpoint("GET", Defaults.AuthPolicyPath)]
[RequirePolicy("same-tenant")]
public class AuthPolicyHandler : AuthEchoHandler
{
}
