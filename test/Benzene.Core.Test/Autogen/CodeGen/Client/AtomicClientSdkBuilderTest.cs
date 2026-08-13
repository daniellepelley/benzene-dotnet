using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Benzene.CodeGen.Client;
using Benzene.Test.Autogen.CodeGen.Helpers;
using Benzene.Test.Autogen.CodeGen.Model;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

public class AtomicClientSdkBuilderTest
{
    private const string BaseNameSpace = "Benzene.Service.Clients";

    private static IDictionary<string, string> Build(IDictionary<string, (Type, Type, Type)> topics)
    {
        var builder = new AtomicClientSdkBuilder(BaseNameSpace);
        return builder.Build(topics.ToEventServiceDocument());
    }

    [Fact]
    public void EmitsOneClientPerTopic_NamedPerTopic()
    {
        var result = Build(new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(string)) },
        });

        // Each topic gets its own self-contained client folder + interface, named from the topic
        // (UserGet, UserCreate), rather than one shared UserServiceClient covering every topic.
        Assert.Contains("UserGet/UserGetServiceClient.cs", result.Keys);
        Assert.Contains("UserGet/IUserGetServiceClient.cs", result.Keys);
        Assert.Contains("UserCreate/UserCreateServiceClient.cs", result.Keys);
        Assert.Contains("UserCreate/IUserCreateServiceClient.cs", result.Keys);
    }

    [Fact]
    public void EachClient_ScopesMethodAndRequiredTopics_ToItsOwnTopic()
    {
        var result = Build(new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(string)) },
        });

        var getClient = result["UserGet/UserGetServiceClient.cs"];

        // The user:get atomic client sends only user:get and validates only that topic at startup —
        // it never references user:create, and never a benzene reserved endpoint either.
        Assert.Contains("\"user:get\"", getClient);
        Assert.DoesNotContain("user:create", getClient);
        Assert.Contains("RequiredTopics = { \"user:get\" }", getClient);

        // Exactly one topic method — its two overloads (with and without headers) — and nothing else:
        // no health check tagging along beside it.
        var methods = getClient.Split('\n').Where(line => line.Contains("public Task<") || line.Contains("public async Task<")).ToArray();
        Assert.Equal(2, methods.Length);
        Assert.All(methods, method => Assert.Contains("GetUserAsync", method));
    }

    [Fact]
    public void OnlyGeneratesDtosReachableFromTheTopic()
    {
        // Two topics with disjoint payloads: user:get ⇒ UserDto, tenant:get ⇒ TenantDto. Each atomic
        // client must carry only its own DTO, not the other topic's — proving schema filtering works.
        var result = Build(new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "tenant:get", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantDto)) },
        });

        // Each client folder carries only its own topic's DTO, not the other topic's.
        Assert.Contains("UserGet/UserDto.cs", result.Keys);
        Assert.Contains("TenantGet/TenantDto.cs", result.Keys);
        Assert.DoesNotContain("UserGet/TenantDto.cs", result.Keys);
        Assert.DoesNotContain("TenantGet/UserDto.cs", result.Keys);
        // The hash embedded in each client is topic-scoped, so the two clients have different hashes.
        var userHash = HashLine(result["UserGet/UserGetServiceClient.cs"]);
        var tenantHash = HashLine(result["TenantGet/TenantGetServiceClient.cs"]);
        Assert.NotEqual(userHash, tenantHash);
    }

    [Fact]
    public void SkipsReservedTopics_ByDefault()
    {
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        }.ToEventServiceDocument();
        document.Requests.First(x => x.Topic == "user:get").Reserved = true;

        var result = new AtomicClientSdkBuilder(BaseNameSpace).Build(document);

        Assert.DoesNotContain("UserGet/UserGetServiceClient.cs", result.Keys);
    }

    // Phase 3b: ClientSdkOptions-driven topic scoping/namespace configuration. See
    // work/spec-mesh-tooling-implementation-plan.md Phase 3b step 7.

    [Fact]
    public void Topics_ScopesWhichPerTopicClientsExist()
    {
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(string)) },
        }.ToEventServiceDocument();

        var options = new ClientSdkOptions { Namespace = BaseNameSpace, Topics = new[] { "user:get" } };
        var result = new AtomicClientSdkBuilder(options).Build(document);

        Assert.Contains("UserGet/UserGetServiceClient.cs", result.Keys);
        Assert.DoesNotContain("UserCreate/UserCreateServiceClient.cs", result.Keys);
    }

    [Fact]
    public void Topics_UnknownTopic_Throws_NamingTheDocumentsValidTopics()
    {
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        }.ToEventServiceDocument();

        var options = new ClientSdkOptions { Namespace = BaseNameSpace, Topics = new[] { "user:delete" } };
        var builder = new AtomicClientSdkBuilder(options);

        var exception = Assert.Throws<ArgumentException>(() => builder.BuildCodeFiles(document));
        Assert.Contains("user:delete", exception.Message);
        Assert.Contains("user:get", exception.Message);
    }

    [Fact]
    public void ExplicitNamespace_AppendsClientNameBelowTheSuppliedRoot()
    {
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        }.ToEventServiceDocument();

        var options = new ClientSdkOptions { Namespace = "Acme.Orders.Clients" };
        var result = new AtomicClientSdkBuilder(options).Build(document);

        Assert.Contains("namespace Acme.Orders.Clients.UserGet", result["UserGet/UserGetServiceClient.cs"]);
    }

    [Fact]
    public void IncludeReservedTopics_RestoresReservedClients()
    {
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "benzene:mesh", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        }.ToEventServiceDocument();

        var excluded = new AtomicClientSdkBuilder(new ClientSdkOptions { Namespace = BaseNameSpace }).Build(document);
        Assert.Empty(excluded);

        var included = new AtomicClientSdkBuilder(new ClientSdkOptions { Namespace = BaseNameSpace, IncludeReservedTopics = true }).Build(document);
        Assert.Contains("BenzeneMesh/BenzeneMeshServiceClient.cs", included.Keys);
    }

    [Fact]
    public void HealthcheckTopic_GetsNoClientOfItsOwn_AndNoClientCarriesAHealthCheck()
    {
        // benzene:healthcheck is an ordinary reserved endpoint: excluded by default like every other
        // benzene:* topic, so a document that declares it spawns no health-check client — and the
        // domain client it sits alongside carries neither a health check nor a benzene RequiredTopics
        // entry, which is what used to fail a consumer's outbound-routing start-up check.
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "benzene:healthcheck", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        }.ToEventServiceDocument();

        var result = new AtomicClientSdkBuilder(new ClientSdkOptions { Namespace = BaseNameSpace }).Build(document);

        Assert.DoesNotContain(result.Keys, name => name.Contains("Healthcheck", StringComparison.OrdinalIgnoreCase));
        var getClient = result["UserGet/UserGetServiceClient.cs"];
        Assert.Contains("RequiredTopics = { \"user:get\" }", getClient);
        Assert.DoesNotContain("benzene:", getClient);
        Assert.DoesNotContain("HealthCheck", getClient);
    }

    // The generated DI registration (dogfooding finding 7c). topic-client mode emits BOTH halves: a
    // per-client extension inside each self-contained client folder, and one aggregate at the root.

    private const string ServiceName = "User";

    private static readonly Dictionary<string, (Type, Type, Type)> TwoTopics = new()
    {
        { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(string)) },
    };

    private static IDictionary<string, string> BuildNamed(IDictionary<string, (Type, Type, Type)> topics) =>
        new AtomicClientSdkBuilder(new ClientSdkOptions { ServiceName = ServiceName, Namespace = BaseNameSpace })
            .Build(topics.ToEventServiceDocument());

    private static string LoadExpected(string fileName) =>
        File.ReadAllText($"{Directory.GetCurrentDirectory()}/Autogen/CodeGen/Client/Examples/{fileName}.txt");

    [Fact]
    public void EachClientFolder_CarriesItsOwnScopedRegistration_MatchingTheGoldenFile()
    {
        var expected = LoadExpected("LambdaService_UserGet_Registration");

        var result = Build(TwoTopics);

        // Self-contained: a consumer that drops in one folder for one topic gets that topic's
        // registration with it, no root file required.
        Assert.Equal(expected, result["UserGet/UserGetServiceClientRegistration.cs"], ignoreLineEndingDifferences: true);
        Assert.Contains("UserCreate/UserCreateServiceClientRegistration.cs", result.Keys);
    }

    [Fact]
    public void AggregateRegistration_RegistersEveryTopicClient_MatchingTheGoldenFile()
    {
        var expected = LoadExpected("LambdaService_User_ClientsRegistration");

        var result = BuildNamed(TwoTopics);

        Assert.Equal(expected, result["UserClientsRegistration.cs"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void AggregateRegistration_DelegatesToEachClientsOwnExtension_OnBenzenesContainer()
    {
        var aggregate = BuildNamed(TwoTopics)["UserClientsRegistration.cs"];

        Assert.Contains("public static IBenzeneServiceContainer AddUserClients(this IBenzeneServiceContainer container)", aggregate);
        Assert.Contains("container.AddUserGetServiceClient();", aggregate);
        Assert.Contains("container.AddUserCreateServiceClient();", aggregate);
        // It can only call them if it can see them - one using per per-topic client namespace.
        Assert.Contains($"using {BaseNameSpace}.UserGet;", aggregate);
        Assert.Contains($"using {BaseNameSpace}.UserCreate;", aggregate);
        Assert.DoesNotContain("IServiceCollection", aggregate);
    }

    [Fact]
    public void AggregateRegistration_IsSkipped_WhenNoServiceNameToNameItAfter()
    {
        // ServiceName names no atomic client (each is named from its topic), so without one there is
        // nothing sensible to call the aggregate - the per-client extensions still ship.
        var result = Build(TwoTopics);

        Assert.DoesNotContain(result.Keys, name => name.EndsWith("ClientsRegistration.cs"));
        Assert.Contains("UserGet/UserGetServiceClientRegistration.cs", result.Keys);
    }

    [Fact]
    public void AggregateRegistration_IsSkipped_WhenNoClientsAreGenerated()
    {
        var document = new Dictionary<string, (Type, Type, Type)>
        {
            { "benzene:mesh", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        }.ToEventServiceDocument();

        var result = new AtomicClientSdkBuilder(new ClientSdkOptions { ServiceName = ServiceName, Namespace = BaseNameSpace })
            .Build(document);

        Assert.Empty(result);
    }

    private static string HashLine(string clientSource) =>
        clientSource.Split('\n').First(line => line.Contains("HashCode =>"));
}
