using System.Collections.Generic;
using System.IO;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.ApiGateway;
using Benzene.CodeGen.Core;
using Benzene.Core.Exceptions;
using Benzene.Core.MessageHandlers;
using Benzene.Http.Routing;
using Benzene.Schema.OpenApi.EventService;
using Benzene.Test.Autogen.CodeGen.Model;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Benzene.Test.Autogen.CodeGen.ApiGateway;

public class LambdaOpenApiBuilderTest
{
    private string LoadExpected(string fileName) => File.ReadAllText($"{Directory.GetCurrentDirectory()}/Autogen/CodeGen/ApiGateway/Examples/{fileName}.yaml");
        
    [Fact]
    public void BuildsSdk_UserGet_Test()
    {
        var expected = LoadExpected("GetUser");

        var messageHandlerDefinitions = new IMessageHandlerDefinition []{
            MessageHandlerDefinition.CreateInstance("user:get", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
            MessageHandlerDefinition.CreateInstance("user:update", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto))
        };

        var httpEndpointDefinitions = new[] {
            HttpEndpointDefinition.CreateInstance("GET", "rbac/user/{id}", "user:get"),
            HttpEndpointDefinition.CreateInstance("PUT", "rbac/user/{id}", "user:update")
        };

        var eventServiceDocument = httpEndpointDefinitions.ToEventServiceDocument(messageHandlerDefinitions);
            
        var apiGatewayBuilderV1 = new ApiGatewayBuilderV1("BENZENE_MARKETPLACE_CORE_FUNC_URI");

        var result = apiGatewayBuilderV1.BuildCodeFiles(eventServiceDocument).ToFilesDictionary();

        Assert.Equal(expected, result["openApi.yaml"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildsSdk_RbacTest_Test()
    {
        var expected = LoadExpected("RbacTest");

        var messageHandlerDefinitions = new IMessageHandlerDefinition[]{
            MessageHandlerDefinition.CreateInstance("rbac:test", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
        };

        var httpEndpointDefinitions = new[] {
            HttpEndpointDefinition.CreateInstance("GET", "rbac/test", "rbac:test"),
        };

        var eventServiceDocument = httpEndpointDefinitions.ToEventServiceDocument(messageHandlerDefinitions);


        var apiGatewayBuilderV1 = new ApiGatewayBuilderV1("BENZENE_RBAC_BFF_FUNC_URI");

        var result = apiGatewayBuilderV1.BuildCodeFiles(eventServiceDocument).ToFilesDictionary();

        Assert.Equal(expected, result["openApi.yaml"], ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildVerb_LiteralSegmentAfterParameter_ResourceStopsAtTheParameter()
    {
        // The emitted "resource" is the static path prefix up to the first path parameter. A literal
        // segment that follows a parameter (users/{id}/orders) must NOT leak into it. The original
        // TakeWhile was dead code (parameter segments are filtered out before it runs), so a
        // mid-path parameter produced "/users/orders/".
        var verb = new ApiGatewayBuilderV1("URI").BuildVerb("GET", "users/{id}/orders", "orders:list");

        Assert.Contains("\"resource\": \"/users/\",", verb);
        Assert.DoesNotContain("/users/orders/", verb);
    }

    [Fact]
    public void BuildVerb_TrailingParameter_ResourceIsTheLiteralPrefix()
    {
        // Regression guard: a trailing parameter (the shape the golden files cover) is unchanged.
        var verb = new ApiGatewayBuilderV1("URI").BuildVerb("GET", "rbac/user/{id}", "user:get");

        Assert.Contains("\"resource\": \"/rbac/user/\",", verb);
    }

    [Fact]
    public void Options_ApplyAuthorizerIdentityHeadersAndExclusions()
    {
        var messageHandlerDefinitions = new IMessageHandlerDefinition[]{
            MessageHandlerDefinition.CreateInstance("user:get", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
            MessageHandlerDefinition.CreateInstance("user:signup", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
        };
        var httpEndpointDefinitions = new[] {
            HttpEndpointDefinition.CreateInstance("GET", "user/{id}", "user:get"),
            HttpEndpointDefinition.CreateInstance("POST", "signup", "user:signup"),
        };
        var eventServiceDocument = httpEndpointDefinitions.ToEventServiceDocument(messageHandlerDefinitions);

        var options = new ApiGatewayOptions("MY_FUNC_URI")
        {
            AuthorizerName = "MyCustomAuthoriser",
            UnauthenticatedTopics = new[] { "user:signup" },
            AllowedHeaders = "Authorization,Content-Type,X-Api-Key,X-Tenant-Id",
            IdentityHeaders = new Dictionary<string, string> { ["x-user-id"] = "$context.authorizer.userid" },
        };

        var result = new ApiGatewayBuilderV1(options).BuildCodeFiles(eventServiceDocument).ToFilesDictionary();
        var yaml = result["openApi.yaml"];

        // Secured topic carries the configured authorizer; the excluded (public) topic does not.
        Assert.Contains("- MyCustomAuthoriser: []", yaml);
        Assert.Contains("- api_key: []", yaml);
        // The excluded topic's operation has api_key but no custom authorizer line above it.
        Assert.DoesNotContain("PlatformTenantId", yaml);
        // Configured identity header and allow-headers are injected; no hard-coded company values remain.
        Assert.Contains("\"x-user-id\":\"$context.authorizer.userid\"", yaml);
        Assert.Contains("'Authorization,Content-Type,X-Api-Key,X-Tenant-Id'", yaml);
    }

    [Fact]
    public void Default_HasNoCustomAuthorizer_AndNoInjectedIdentityHeaders()
    {
        var messageHandlerDefinitions = new IMessageHandlerDefinition[]{
            MessageHandlerDefinition.CreateInstance("user:get", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
        };
        var httpEndpointDefinitions = new[] {
            HttpEndpointDefinition.CreateInstance("GET", "user/{id}", "user:get"),
        };
        var eventServiceDocument = httpEndpointDefinitions.ToEventServiceDocument(messageHandlerDefinitions);

        var yaml = new ApiGatewayBuilderV1("MY_FUNC_URI").BuildCodeFiles(eventServiceDocument).ToFilesDictionary()["openApi.yaml"];

        // The generic default is company-free: api_key only, and only transport identity headers.
        Assert.Contains("- api_key: []", yaml);
        Assert.DoesNotContain("Authoriser", yaml);
        Assert.DoesNotContain("$context.authorizer", yaml);
        Assert.Contains("\"UserAgent\": \"$context.identity.userAgent\"", yaml);
    }

    [Fact]
    public void BuildCodeFiles_TwoTopicsShareAMethodAndPath_FailsLoudly_NotDuplicateKeyYaml()
    {
        // Two topics both mapped to GET user/{id} used to fall straight through into duplicate-key
        // YAML (two "get:" blocks under one path) and a corrupted CORS header
        // ('GET,GET,OPTIONS'). Mirrors ReflectionHttpEndpointFinder's own duplicate-route
        // fail-fast: a method+path collision is a spec authoring error the generator should refuse
        // to paper over.
        var messageHandlerDefinitions = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("user:get", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
            MessageHandlerDefinition.CreateInstance("user:get-legacy", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
        };
        var httpEndpointDefinitions = new[]
        {
            HttpEndpointDefinition.CreateInstance("GET", "user/{id}", "user:get"),
            HttpEndpointDefinition.CreateInstance("GET", "user/{id}", "user:get-legacy"),
        };
        var eventServiceDocument = httpEndpointDefinitions.ToEventServiceDocument(messageHandlerDefinitions);

        var builder = new ApiGatewayBuilderV1("MY_FUNC_URI");

        var exception = Assert.Throws<BenzeneException>(() => builder.BuildCodeFiles(eventServiceDocument));

        Assert.Contains("GET", exception.Message);
        Assert.Contains("user/{id}", exception.Message);
    }

    [Fact]
    public void BuildOptions_RepeatedVerb_CorsHeaderIsDeduped_NotRepeated()
    {
        // Defense-in-depth: BuildOptions is public and callable directly with a caller-supplied verb
        // list, independent of BuildCodeFiles' fail-fast guard.
        var options = new ApiGatewayBuilderV1("URI").BuildOptions(new[] { "GET", "GET" }, "user/{id}", "user:get");

        Assert.Contains("'GET,OPTIONS'", options);
        Assert.DoesNotContain("'GET,GET,OPTIONS'", options);
    }

    // #211: the duplicate-route guard used to group on raw Method (case-sensitive) while BuildVerb
    // lowercases the verb for emission, so "GET" and "get" mapped to the same path passed the check
    // and then collided as two identical `get:` keys under that path in the emitted YAML - mirrors
    // ReflectionHttpEndpointFinder's own case-folded duplicate-route check for the identical concern.
    [Fact]
    public void BuildCodeFiles_TwoTopicsShareAPathWithDifferentlyCasedMethod_FailsLoudly_NotDuplicateKeyYaml()
    {
        var messageHandlerDefinitions = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("user:get", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
            MessageHandlerDefinition.CreateInstance("user:get-legacy", typeof(GetUserMessage), typeof(GetUserMessage), typeof(UserDto)),
        };
        var httpEndpointDefinitions = new[]
        {
            HttpEndpointDefinition.CreateInstance("GET", "user/{id}", "user:get"),
            HttpEndpointDefinition.CreateInstance("get", "user/{id}", "user:get-legacy"),
        };
        var eventServiceDocument = httpEndpointDefinitions.ToEventServiceDocument(messageHandlerDefinitions);

        var builder = new ApiGatewayBuilderV1("MY_FUNC_URI");

        var exception = Assert.Throws<BenzeneException>(() => builder.BuildCodeFiles(eventServiceDocument));

        // Whichever of the two differently-cased endpoints happened to be encountered first names
        // the route in the message - case-insensitive check keeps this independent of that ordering.
        Assert.Contains("GET", exception.Message.ToUpperInvariant());
        Assert.Contains("user/{id}", exception.Message);
        Assert.Contains("user:get", exception.Message);
        Assert.Contains("user:get-legacy", exception.Message);
    }

    // #212/#263: a message topic (summary/tag source) or a path segment (tag source, and the path
    // mapping key itself) can contain a `"`, `:` or `'` - unescaped, these used to break the
    // double-quoted `summary:` scalar or survive title-casing into an invalid unquoted `tags:`
    // sequence item. YamlLiteral's single-quote-and-escape now guards every such value.
    [Fact]
    public void BuildVerb_AdversarialTopic_QuoteAndBackslash_SafelySingleQuoted()
    {
        const string adversarialTopic = "user:get\" # inject: value";

        var verb = new ApiGatewayBuilderV1("URI").BuildVerb("GET", "user/{id}", adversarialTopic);

        Assert.Contains($"summary: {YamlLiteral.Format(adversarialTopic)}", verb);
        // The pre-fix raw double-quoted interpolation would have produced this exact broken form.
        Assert.DoesNotContain($@"summary: ""{adversarialTopic}""", verb);
    }

    [Fact]
    public void BuildPath_AdversarialPath_ColonAndQuote_KeyAndTagSafelySingleQuoted()
    {
        // A `:` surviving title-casing (CreateTag upper-cases each segment but leaves punctuation
        // alone) into an unquoted YAML sequence item is the exact #212 reproduction.
        const string adversarialPath = "user/weird:segment";

        var built = new ApiGatewayBuilderV1("URI").BuildPath(adversarialPath, new[] { ("GET", "user:get") });

        Assert.Contains($"  {YamlLiteral.Format("/" + adversarialPath)}:", built);
        Assert.DoesNotContain($"  /{adversarialPath}:", built);
    }

    [Fact]
    public void BuildVerb_TopicContainingAQuote_ProducesValidYaml_WithTheValueIntact()
    {
        // #212: the topic was interpolated raw into a double-quoted `summary:` scalar. A `"` in the
        // topic broke the scalar out of the string, producing YAML a real parser can't load.
        var topic = "user:get \"legacy\" variant";

        var document = WrapAsDocument(new ApiGatewayBuilderV1("URI").BuildVerb("GET", "user/{id}", topic));

        var yaml = new YamlStream();
        yaml.Load(new StringReader(document));

        var summary = FindScalar(yaml, "summary");
        Assert.Equal(topic, summary);
    }

    [Fact]
    public void BuildOptions_PathSegmentContainingColonSpace_ProducesValidYaml_WithTheTagIntact()
    {
        // #212: CreateTag title-cases each path segment and joins them into one string emitted as
        // an unquoted YAML sequence item under `tags:`. A ": " surviving that title-casing (e.g. a
        // literal path segment containing it) produces something that no longer parses as a single
        // scalar - YAML reads a bare "key: value" shape inside a sequence item as a nested mapping.
        var path = "orders/on: hold/{id}";

        var document = WrapAsDocument(new ApiGatewayBuilderV1("URI").BuildOptions(new[] { "GET" }, path, "orders:get"));

        var yaml = new YamlStream();
        yaml.Load(new StringReader(document));

        var tag = FindFirstSequenceItem(yaml, "tags");
        Assert.Equal("Orders On: Hold", tag);
    }

    // BuildVerb/BuildOptions emit fragments indented as if nested under a path mapping (they are,
    // in BuildCodeFiles' real output) - wrap in a minimal enclosing document so the fragment is
    // itself a structurally complete, loadable YAML document.
    private static string WrapAsDocument(string fragment) => "root:\n" + fragment;

    private static string FindScalar(YamlStream yaml, string key)
    {
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return FindScalarRecursive(root, key) ?? throw new Xunit.Sdk.XunitException($"Key '{key}' not found");
    }

    private static string? FindScalarRecursive(YamlNode node, string key)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var entry in mapping.Children)
            {
                if (entry.Key is YamlScalarNode scalarKey && scalarKey.Value == key && entry.Value is YamlScalarNode scalarValue)
                {
                    return scalarValue.Value;
                }

                var found = FindScalarRecursive(entry.Value, key);
                if (found != null)
                {
                    return found;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children)
            {
                var found = FindScalarRecursive(item, key);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string FindFirstSequenceItem(YamlStream yaml, string key)
    {
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return FindSequenceItemRecursive(root, key) ?? throw new Xunit.Sdk.XunitException($"Key '{key}' not found");
    }

    private static string? FindSequenceItemRecursive(YamlNode node, string key)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var entry in mapping.Children)
            {
                if (entry.Key is YamlScalarNode scalarKey && scalarKey.Value == key && entry.Value is YamlSequenceNode sequenceValue)
                {
                    return ((YamlScalarNode)sequenceValue.Children[0]).Value;
                }

                var found = FindSequenceItemRecursive(entry.Value, key);
                if (found != null)
                {
                    return found;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children)
            {
                var found = FindSequenceItemRecursive(item, key);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
