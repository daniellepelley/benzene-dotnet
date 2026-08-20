using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Test.Autogen.CodeGen.Helpers;
using Benzene.Test.Autogen.CodeGen.Model;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

public class MessageClientSdkBuilderTest
{
    private const string BaseNameSpace = "Benzene.Service.Clients";
    private const string UserServiceName = "User";
    private const string TenantServiceName = "Tenant";

    private string LoadExpected(string fileName) => File.ReadAllText($"{Directory.GetCurrentDirectory()}/Autogen/CodeGen/Client/Examples/{fileName}.txt");

    [Fact]
    public void BuildsSdk_UserGet_Test()
    {
        var expected = LoadExpected("LambdaService_UserGet");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(MessageWrapper<UserDto>)) }
        };

        var lambdaServiceSdkBuilder = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument());

        Assert.Equal(expected, result["UserServiceClient.cs"], ignoreLineEndingDifferences: true);
    }
        
    [Fact]
    public void BuildsSdk_UserCreate_Test()
    {
        var expected = LoadExpected("LambdaService_UserCreate");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(Guid?)) }
        };

        var lambdaServiceSdkBuilder = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument());

        Assert.Equal(expected, result["UserServiceClient.cs"], ignoreLineEndingDifferences: true);
    }
        
    [Fact]
    public void BuildsSdk_UserFull_Test()
    {
        var expectedClass = LoadExpected("LambdaService_UserFull");
        var expectedInterface = LoadExpected("LambdaService_UserFull_Interface");
        var expectedGetUserMessage = LoadExpected("LambdaService_GetUserMessage");
        var expectedCreateUserMessage = LoadExpected("LambdaService_CreateUserMessage");
        var expectedUserDto = LoadExpected("LambdaService_UserDto");
        var expectedInternalDto = LoadExpected("LambdaService_InternalDto");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(string)) }
        };

        var lambdaServiceSdkBuilder = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument());

        Assert.Equal(expectedClass, result["UserServiceClient.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedInterface, result["IUserServiceClient.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedGetUserMessage, result["GetUserMessage.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedCreateUserMessage, result["CreateUserMessage.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedCreateUserMessage, result["CreateUserMessage.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedUserDto, result["UserDto.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedInternalDto, result["InternalDto.cs"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildsSdk_GetUserMessage_Test()
    {
        var expected = LoadExpected("LambdaService_GetUserMessage");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) }
        };

        var lambdaServiceSdkBuilder = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument());

        Assert.Equal(expected, result["GetUserMessage.cs"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildsSdk_CreateUserMessage_Test()
    {
        var expected = LoadExpected("LambdaService_CreateUserMessage");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(Guid?)) }
        };

        var lambdaServiceSdkBuilder = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToMessageHandlerDefinitions());

        Assert.Equal(expected, result["CreateUserMessage.cs"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildsSdk_TenantFull_Test()
    {
        var expectedClass = LoadExpected("LambdaService_TenantFull");
        var expectedInterface = LoadExpected("LambdaService_TenantFull_Interface");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "tenant:get", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantDto)) },
            { "tenant:create", (typeof(CreateTenantMessage), typeof(CreateTenantMessage), typeof(TenantDto)) }
        };

        var lambdaServiceSdkBuilder = new MessageClientSdkBuilder(TenantServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument());

        Assert.Equal(expectedClass, result["TenantServiceClient.cs"], ignoreLineEndingDifferences: true);
        Assert.Equal(expectedInterface, result["ITenantServiceClient.cs"], ignoreLineEndingDifferences: true);
    }

    // Phase 3b: ClientSdkOptions-driven topic scoping/namespace configuration. See
    // work/archive/spec-mesh-tooling-implementation-plan-2026-08.md Phase 3b step 7.

    private static readonly Dictionary<string, (Type, Type, Type)> TwoTopics = new()
    {
        { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        { "user:create", (typeof(CreateUserMessage), typeof(CreateUserMessage), typeof(string)) },
    };

    [Fact]
    public void Topics_ScopesMethods_Interface_AndRequiredTopics_Together()
    {
        var options = new ClientSdkOptions
        {
            ServiceName = UserServiceName,
            Namespace = $"{BaseNameSpace}.{UserServiceName}",
            Topics = new[] { "user:get" },
        };

        var result = new MessageClientSdkBuilder(options).Build(TwoTopics.ToEventServiceDocument());

        var classSource = result["UserServiceClient.cs"];
        var interfaceSource = result["IUserServiceClient.cs"];

        Assert.Contains("GetUserAsync", classSource);
        Assert.DoesNotContain("CreateUserAsync", classSource);
        Assert.Contains("GetUserAsync", interfaceSource);
        Assert.DoesNotContain("CreateUserAsync", interfaceSource);
        Assert.Contains(@"RequiredTopics = { ""user:get"" }", classSource);
    }

    [Fact]
    public void Topics_UnknownTopic_Throws_NamingTheDocumentsValidTopics()
    {
        var options = new ClientSdkOptions
        {
            ServiceName = UserServiceName,
            Namespace = BaseNameSpace,
            Topics = new[] { "user:delete" },
        };

        var builder = new MessageClientSdkBuilder(options);

        var exception = Assert.Throws<ArgumentException>(() => builder.BuildCodeFiles(TwoTopics.ToEventServiceDocument()));
        Assert.Contains("user:delete", exception.Message);
        Assert.Contains("user:get", exception.Message);
        Assert.Contains("user:create", exception.Message);
    }

    [Fact]
    public void ExplicitNamespace_UsedExactly_OnClientInterfaceAndDtos()
    {
        var options = new ClientSdkOptions
        {
            ServiceName = UserServiceName,
            Namespace = "Acme.Orders.Clients",
        };

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        };

        var result = new MessageClientSdkBuilder(options).Build(dictionary.ToEventServiceDocument());

        // No magic ".User" suffix anywhere - the supplied namespace is used exactly.
        Assert.Contains("namespace Acme.Orders.Clients", result["UserServiceClient.cs"].ToLines());
        Assert.Contains("namespace Acme.Orders.Clients", result["IUserServiceClient.cs"].ToLines());
        Assert.Contains("namespace Acme.Orders.Clients", result["UserDto.cs"].ToLines());
    }

    [Fact]
    public void ReservedTopics_ExcludedByDefault_IncludeReservedTopicsRestoresThem()
    {
        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "benzene:mesh", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        };
        var document = dictionary.ToEventServiceDocument();

        var excluded = new MessageClientSdkBuilder(new ClientSdkOptions { ServiceName = UserServiceName, Namespace = BaseNameSpace })
            .Build(document);
        Assert.DoesNotContain("benzene:mesh", excluded["UserServiceClient.cs"]);

        var included = new MessageClientSdkBuilder(new ClientSdkOptions { ServiceName = UserServiceName, Namespace = BaseNameSpace, IncludeReservedTopics = true })
            .Build(document);
        Assert.Contains(@"""benzene:mesh""", included["UserServiceClient.cs"]);
    }

    [Fact]
    public void BenzeneReservedEndpoints_AreNeverGenerated_NorIsAHealthCheck()
    {
        // Benzene's reserved endpoints are framework plumbing, deliberately separate from a service's
        // domain surface; a generated client covers the domain only. benzene:healthcheck is no
        // exception - it gets no method, no IHasHealthCheck, and above all no RequiredTopics entry,
        // which used to demand an outbound route every consumer had to invent or fail start-up.
        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "benzene:healthcheck", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        };

        var options = new ClientSdkOptions
        {
            ServiceName = UserServiceName,
            Namespace = BaseNameSpace,
        };

        var result = new MessageClientSdkBuilder(options).Build(dictionary.ToEventServiceDocument());
        var classSource = result["UserServiceClient.cs"];
        var interfaceSource = result["IUserServiceClient.cs"];

        Assert.DoesNotContain("benzene:", classSource);
        Assert.DoesNotContain("HealthCheck", classSource);
        Assert.DoesNotContain("IHasHealthCheck", interfaceSource);
        Assert.DoesNotContain("HealthCheck", interfaceSource);
        Assert.Contains(@"RequiredTopics = { ""user:get"" }", classSource);
        // The contract hash stays - it is informative and consumers read it off the client.
        Assert.Contains("public string HashCode =>", classSource);
    }

    // The generated DI registration. See work/archive/spec-mesh-tooling-implementation-plan-2026-08.md's dogfooding
    // finding 7c: every consumer used to hand-write the registration and had to know to use Scoped.

    [Fact]
    public void EmitsDiRegistrationExtension_MatchingTheGoldenFile()
    {
        var expected = LoadExpected("LambdaService_UserFull_Registration");

        var result = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace).Build(TwoTopics.ToEventServiceDocument());

        Assert.Equal(expected, result["UserServiceClientRegistration.cs"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void DiRegistration_TargetsBenzenesOwnContainer_NotIServiceCollection()
    {
        // The ruling: register against IBenzeneServiceContainer. A consumer may be on Autofac or any
        // other container - if Benzene is doing the DI, an IServiceCollection extension is useless to
        // them.
        var result = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace).Build(TwoTopics.ToEventServiceDocument());
        var registration = result["UserServiceClientRegistration.cs"];

        Assert.Contains("using Benzene.Abstractions.DI;", registration);
        Assert.Contains("this IBenzeneServiceContainer container", registration);
        Assert.DoesNotContain("IServiceCollection", registration);
        Assert.DoesNotContain("Microsoft", registration);
    }

    [Fact]
    public void DiRegistration_IsScoped_AndSaysWhy()
    {
        // Scoped is required, not a preference: AddOutboundRouting registers IBenzeneMessageSender
        // scoped, so a singleton client would capture it. The generated file carries the reason.
        var result = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace).Build(TwoTopics.ToEventServiceDocument());
        var registration = result["UserServiceClientRegistration.cs"];

        Assert.Contains("AddScoped<IUserServiceClient, UserServiceClient>()", registration);
        Assert.DoesNotContain("AddSingleton", registration);
        Assert.DoesNotContain("AddTransient", registration);
        Assert.Contains("captive dependency", registration);
    }

    [Fact]
    public void DiRegistration_LandsInTheSameNamespaceAsTheClient()
    {
        var options = new ClientSdkOptions { ServiceName = UserServiceName, Namespace = "Acme.Orders.Clients" };

        var result = new MessageClientSdkBuilder(options).Build(TwoTopics.ToEventServiceDocument());
        var registration = result["UserServiceClientRegistration.cs"];

        Assert.Contains("namespace Acme.Orders.Clients", registration.ToLines());
        Assert.Contains("public static IBenzeneServiceContainer AddUserServiceClient", registration);
    }

    [Fact]
    public void DiRegistration_AddsNoNewPackageDependency_OnlyBenzeneAbstractions()
    {
        // Benzene.Abstractions is already referenced by the generated client (IBenzeneResult), so the
        // registration must not reach for anything the generated output doesn't already need.
        var result = new MessageClientSdkBuilder(UserServiceName, BaseNameSpace).Build(TwoTopics.ToEventServiceDocument());

        var usings = result["UserServiceClientRegistration.cs"].ToLines()
            .Where(line => line.StartsWith("using "))
            .ToArray();

        Assert.Equal(new[] { "using System.Diagnostics.CodeAnalysis;", "using Benzene.Abstractions.DI;" }, usings);
    }
}

