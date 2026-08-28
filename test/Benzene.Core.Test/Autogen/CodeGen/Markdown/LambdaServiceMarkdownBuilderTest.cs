using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Benzene.CodeGen.Markdown;
using Benzene.FluentValidation.Schema;
using Benzene.Schema.OpenApi;
using Benzene.Schema.OpenApi.EventService;
using Benzene.Test.Autogen.CodeGen.Helpers;
using Benzene.Test.Autogen.CodeGen.Model;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Markdown;

public class LambdaServiceMarkdownBuilderTest
{
    private const string BaseNameSpace = "This is the header";

    private const string UserLambdaName = "benzene-user-core-func";
    private const string UserServiceName = "User";
        
    private const string TenantLambdaName = "benzene-tenant-core-func";
    private const string TenantServiceName = "Tenant";

    private string LoadExpected(string fileName) => File.ReadAllText($"{Directory.GetCurrentDirectory()}/Autogen/CodeGen/Markdown/Examples/{fileName}.md");
        
    [Fact]
    public void BuildsSdk_UserGet_Test()
    {
        var expected =LoadExpected("User_Get");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)) }
        };

        var fluentValidationSchemaBuilder = new FluentValidationSchemaBuilder(Assembly.GetExecutingAssembly());
        var lambdaServiceSdkBuilder = new LambdaServiceMarkdownBuilder(UserLambdaName, UserServiceName, BaseNameSpace);

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument(new OpenApiValidationSchemaBuilder(new SchemaBuilder(), fluentValidationSchemaBuilder)));

        Assert.Equal(expected, result["README.md"], ignoreLineEndingDifferences: true);
    }
        
    [Fact]
    public void BuildsSdk_UserGetAll_Test()
    {
        var expected =LoadExpected("User_GetAll");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "user:get", (typeof(GetAllUserMessage), typeof(GetAllUserMessage), typeof(UserDto[])) }
        };

        var fluentValidationSchemaBuilder = new FluentValidationSchemaBuilder(Assembly.GetExecutingAssembly());
        var lambdaServiceSdkBuilder = new LambdaServiceMarkdownBuilder(UserLambdaName, UserServiceName, BaseNameSpace); 

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument(new OpenApiValidationSchemaBuilder(new SchemaBuilder(), fluentValidationSchemaBuilder)));

        Assert.Equal(expected, result["README.md"], ignoreLineEndingDifferences: true);
    }
        
    [Fact]
    public void BuildsSdk_TenantGet_Test()
    {
        var expected = LoadExpected("Tenant_Get");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "tenant:get", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantDto)) }
        };

        var fluentValidationSchemaBuilder = new FluentValidationSchemaBuilder(Assembly.GetExecutingAssembly());
        var lambdaServiceSdkBuilder = new LambdaServiceMarkdownBuilder(TenantLambdaName, TenantServiceName, BaseNameSpace); 

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument(new OpenApiValidationSchemaBuilder(new SchemaBuilder(), fluentValidationSchemaBuilder)));

        Assert.Equal(expected, result["README.md"], ignoreLineEndingDifferences: true);
    }
        
    [Fact]
    public void BuildsSdk_TenantFull_Test()
    {
        var expected = LoadExpected("Tenant_Full");

        var dictionary = new Dictionary<string, (Type, Type, Type)>
        {
            { "tenant:get", (typeof(GetTenantMessage), typeof(GetTenantMessage), typeof(TenantDto)) },
            { "tenant:create", (typeof(CreateTenantMessage), typeof(CreateTenantMessage), typeof(TenantDto)) }
        };

        var fluentValidationSchemaBuilder = new FluentValidationSchemaBuilder(Assembly.GetExecutingAssembly());
        var lambdaServiceSdkBuilder = new LambdaServiceMarkdownBuilder(TenantLambdaName, TenantServiceName, BaseNameSpace); 

        var result = lambdaServiceSdkBuilder.Build(dictionary.ToEventServiceDocument(new OpenApiValidationSchemaBuilder(new SchemaBuilder(), fluentValidationSchemaBuilder)));

        Assert.Equal(expected, result["README.md"], ignoreLineEndingDifferences: true);
    }

    // #265: BuildValidation embeds property names/rules straight into a Markdown table row with no
    // `|` escaping - a property name containing a pipe (reachable via any hand-authored/deserialized
    // schema, same class as #213's MapProperty) used to be read as an extra table cell boundary,
    // corrupting the rendered row.
    [Fact]
    public void BuildValidation_PropertyNameContainingPipe_RendersAsACorrectTableRow()
    {
        var requestSchema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["bad|name"] = new OpenApiSchema { Type = "string", Nullable = false }
            }
        };
        var responseSchema = new OpenApiSchema { Type = "object" };

        var document = new EventServiceDocument(
            new OpenApiInfo(),
            Array.Empty<OpenApiTag>(),
            new[] { new RequestResponse { Topic = "user:get", Request = requestSchema, Response = responseSchema } },
            Array.Empty<Event>(),
            new OpenApiComponents { Schemas = new Dictionary<string, OpenApiSchema>() });

        var builder = new LambdaServiceMarkdownBuilder(UserLambdaName, UserServiceName, BaseNameSpace);
        var readme = builder.Build(document)["README.md"];

        Assert.Contains(@"|bad\|name|Not Null|", readme);
        // The un-escaped, table-corrupting form must not appear.
        Assert.DoesNotContain("|bad|name|Not Null|", readme);
    }
}

