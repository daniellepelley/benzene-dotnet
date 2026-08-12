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
    // work/spec-mesh-tooling-implementation-plan.md Phase 3b step 7.

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
        Assert.Contains(@"RequiredTopics = { ""user:get"", ""benzene:healthcheck"" }", classSource);
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
    public void HealthcheckTopic_NeverNeedsNaming_AndNeverDuplicatesInRequiredTopics()
    {
        // Even when the document itself carries an explicit request/response entry for
        // benzene:healthcheck, it must stay out of the ordinary Requests-driven projection:
        // HealthCheckAsync() and its RequiredTopics entry are already emitted unconditionally, so a
        // surviving Requests entry would duplicate "benzene:healthcheck" in RequiredTopics.
        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
            { "benzene:healthcheck", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) },
        };

        var options = new ClientSdkOptions
        {
            ServiceName = UserServiceName,
            Namespace = BaseNameSpace,
            Topics = new[] { "user:get" }, // benzene:healthcheck deliberately not named
        };

        var result = new MessageClientSdkBuilder(options).Build(dictionary.ToEventServiceDocument());
        var requiredTopicsLine = result["UserServiceClient.cs"].ToLines().Single(l => l.Contains("RequiredTopics ="));

        Assert.Equal(1, requiredTopicsLine.Split("benzene:healthcheck").Length - 1);
    }
}

