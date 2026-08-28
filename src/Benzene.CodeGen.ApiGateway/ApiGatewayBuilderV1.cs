using System.Globalization;
using System.Text;
using Benzene.CodeGen.Core;
using Benzene.Core.Exceptions;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.AspNetCore.Routing.Template;

namespace Benzene.CodeGen.ApiGateway
{
    public class ApiGatewayBuilderV1 : ICodeBuilder<EventServiceDocument>
    {
        private const string HeaderIndent = "                ";

        private readonly ApiGatewayOptions _options;

        /// <summary>Initializes the builder with generic defaults (see <see cref="ApiGatewayOptions"/>).</summary>
        /// <param name="url">The backend integration URI token.</param>
        public ApiGatewayBuilderV1(string url)
            : this(new ApiGatewayOptions(url))
        {
        }

        /// <summary>Initializes the builder with explicit options.</summary>
        public ApiGatewayBuilderV1(ApiGatewayOptions options)
        {
            _options = options;
        }

        public ICodeFile[] BuildCodeFiles(EventServiceDocument source)
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"# AUTOGEN START {_options.Url.ToUpperInvariant()}");
            stringBuilder.AppendLine("");

            var mappings = source.Requests
                .Where(x => x.HttpMappings != null)
                .SelectMany(request => request.HttpMappings.Select(http => new { request, http }))
                .ToArray();

            // Two topics sharing a method+path used to fall straight through into duplicate-key YAML
            // (`get:` emitted twice under one path) and a corrupted CORS header
            // (`'GET,GET,OPTIONS'`). Fail loudly instead, mirroring
            // Benzene.Http.Routing.ReflectionHttpEndpointFinder's own duplicate-route check - a
            // method+path collision is a spec authoring error, not something codegen can silently
            // resolve.
            //
            // Case-fold the grouping key's Method (not Path - unlike ReflectionHttpEndpointFinder's
            // route-matching concern, a path here is emitted verbatim as the YAML mapping key, so two
            // paths differing only by case are genuinely two different keys, not a collision) -
            // BuildVerb always emits the verb lower-cased (`verb.ToLowerInvariant()`), so two topics
            // mapped to "GET" and "get" for the same path used to pass this check and then collide as
            // identical `get:` keys under that path in the emitted YAML - the same duplicate-key shape
            // this guard exists to catch, just reached via verb casing instead of identical casing.
            var duplicates = mappings
                .GroupBy(x => new { Method = x.http.Method.ToLowerInvariant(), x.http.Path })
                .Where(x => x.Count() > 1)
                .ToArray();

            if (duplicates.Any())
            {
                var duplicate = duplicates[0];
                var topics = string.Join(", ", duplicate.Select(x => x.request.Topic));
                throw new BenzeneException(
                    $"Route '{duplicate.First().http.Method} - {duplicate.Key.Path}' has been assigned to more than one topic ({topics}), this is not permitted");
            }

            var paths = mappings
                .GroupBy(x => x.http.Path)
                .ToArray();


            foreach (var route in paths)
            {
                stringBuilder.Append(BuildPath(route.Key, route.Select(x => (x.http.Method, x.request.Topic)).ToArray()));
            }

            stringBuilder.AppendLine("");
            stringBuilder.AppendLine("# AUTOGEN END");


            return new ICodeFile[] {
                new CodeFile("openApi.yaml", stringBuilder.ToString().ToLines())
            };
        }


        public string BuildPath(string path, (string, string)[] endpoints)
        {
            var stringBuilder = new StringBuilder();
            // path is user-authored (an [HttpEndpoint] URL) - quote-escape the mapping key rather
            // than emitting it raw, so a `:`/`"`/`#` in a path segment can't corrupt the YAML
            // structure (#212/#263).
            stringBuilder.AppendLine($@"  {YamlLiteral.Format("/" + path)}:");
            stringBuilder.Append(BuildOptions(endpoints.Select(x => x.Item1).ToArray(), path, endpoints[0].Item2));

            foreach (var endpoint in endpoints)
            {
                stringBuilder.Append(BuildVerb(endpoint.Item1, path, endpoint.Item2));
            }

            return stringBuilder.ToString();
        }

        public string BuildOptions(string[] verbs, string path, string topic)
        {
            var routeTemplate = TemplateParser.Parse(path);
            var tag = CreateTag(path);
            // Distinct as defense-in-depth: BuildCodeFiles fails loudly on a method+path collision
            // before this is reached, but BuildOptions/BuildPath are public and can be called
            // directly (e.g. from tests) with a caller-supplied verb list that already repeats a
            // verb - Distinct keeps the CORS header ('GET,OPTIONS', never 'GET,GET,OPTIONS') valid
            // either way.
            var verbsText = string.Join(',', verbs.Select(x => x.ToUpperInvariant()).Distinct());

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine(@"    options:");
            stringBuilder.AppendLine(@"      tags:");
            // tag is title-cased from the user-authored path - quote-escape it so e.g. a `:` in a
            // path segment can't survive title-casing into an invalid unquoted sequence item (#212/#263).
            stringBuilder.AppendLine($@"        - {YamlLiteral.Format(tag)}");
            BuildParameters(routeTemplate, stringBuilder);
            stringBuilder.AppendLine(@"      responses:");
            stringBuilder.AppendLine(@"        ""200"":");
            stringBuilder.AppendLine(@"          $ref: ""#/components/responses/corsResponse""");
            stringBuilder.AppendLine(@"      x-amazon-apigateway-integration:");
            stringBuilder.AppendLine(@"        responses:");
            stringBuilder.AppendLine(@"          default:");
            stringBuilder.AppendLine(@"            statusCode: ""200""");
            stringBuilder.AppendLine(@"            responseParameters:");
            stringBuilder.AppendLine(@"              method.response.header.Cache-Control: ""'no-store, no-cache'""");
            stringBuilder.AppendLine(@"              method.response.header.X-Frame-Options: ""'deny'""");
            stringBuilder.AppendLine(@"              method.response.header.X-XSS-Protection: ""'1; mode=block'""");
            stringBuilder.AppendLine(@"              method.response.header.Strict-Transport-Security: ""'max-age=31536000'""");
            stringBuilder.AppendLine(@"              method.response.header.Content-Security-Policy: ""'default-src \\'none\\';'""");
            stringBuilder.AppendLine(@"              method.response.header.X-Content-Type-Options: ""'nosniff'""");
            stringBuilder.AppendLine(@"              method.response.header.Referrer-Policy: ""'no-referrer'""");
            stringBuilder.AppendLine(@$"              method.response.header.Access-Control-Allow-Methods: ""'{verbsText},OPTIONS'""");
            stringBuilder.AppendLine(@$"              method.response.header.Access-Control-Allow-Headers: ""'{_options.AllowedHeaders}'""");
            stringBuilder.AppendLine(@"            responseTemplates:");
            stringBuilder.AppendLine(@"              application/json: |");
            CorsHeaders(stringBuilder);
            stringBuilder.AppendLine(@"        passthroughBehavior: when_no_match");
            stringBuilder.AppendLine(@"        requestTemplates:");
            stringBuilder.AppendLine(@"          application/json: '{""statusCode"": 200}'");
            stringBuilder.AppendLine(@"        type: mock");
            return stringBuilder.ToString();
        }

        private static void CorsHeaders(StringBuilder stringBuilder)
        {
            stringBuilder.AppendLine(@"                #set($domainWhitelist = [#cors_allowed_origins#])");
            stringBuilder.AppendLine(@"");
            stringBuilder.AppendLine(@"                #if (#cors_localhost#)");
            stringBuilder.AppendLine(@"                  #set($context.responseOverride.header.Access-Control-Allow-Origin = $input.params(""Origin""))");
            stringBuilder.AppendLine(@"                #elseif ($domainWhitelist.contains($input.params(""Origin"")))");
            stringBuilder.AppendLine(
                @"                  #set($context.responseOverride.header.Access-Control-Allow-Origin = $input.params(""Origin""))");
            stringBuilder.AppendLine(@"                #else");
            stringBuilder.AppendLine(@"                  #set($context.responseOverride.status = 405)");
            stringBuilder.AppendLine(@"                #end");
        }

        public string BuildVerb(string verb, string path, string topic)
        {
            var routeTemplate = TemplateParser.Parse(path);

            // The "resource" is the static path prefix up to the first path parameter. Stop at the
            // first segment containing a parameter, using the segment's own IsParameter flag: a
            // parameter part has Text == null, so it would otherwise be flattened to "" and dropped
            // before any check could see it (which made the old TakeWhile dead and leaked literal
            // segments that follow a parameter into the prefix).
            var literalPrefix = routeTemplate.Segments
                .TakeWhile(segment => segment.Parts.All(part => !part.IsParameter))
                .Select(segment => string.Join("", segment.Parts.Select(part => part.Text?.Replace("/", ""))))
                .Where(text => !string.IsNullOrEmpty(text));

            var resource = "/" + string.Join("/", literalPrefix) + "/";

            var tag = CreateTag(path);

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($@"    {verb.ToLowerInvariant()}:");
            // topic is user-authored - a `"` in it used to break this double-quoted scalar (#212/#263).
            stringBuilder.AppendLine($@"      summary: {YamlLiteral.Format(topic)}");
            stringBuilder.AppendLine(@"      tags:");
            stringBuilder.AppendLine($@"        - {YamlLiteral.Format(tag)}");

            BuildParameters(routeTemplate, stringBuilder);

            AddResponses(stringBuilder);
            stringBuilder.AppendLine(@"      security:");
            if (_options.AuthorizerName != null && !_options.UnauthenticatedTopics.Contains(topic))
            {
                stringBuilder.AppendLine($@"        - {_options.AuthorizerName}: []");
            }
            stringBuilder.AppendLine(@"        - api_key: []");
            stringBuilder.AppendLine(@"      x-amazon-apigateway-integration:");
            stringBuilder.AppendLine(@"        type: ""AWS""");
            stringBuilder.AppendLine(@"        httpMethod: POST");
            stringBuilder.AppendLine(@$"        uri: ""#{_options.Url}#""");
            stringBuilder.AppendLine(@"        passthroughBehavior: ""never""");
            stringBuilder.AppendLine(@"        requestTemplates:");
            stringBuilder.AppendLine(@"          application/json: |");
            stringBuilder.AppendLine(@"            {");
            stringBuilder.AppendLine($@"              ""httpMethod"": ""{verb.ToUpperInvariant()}"",");
            stringBuilder.AppendLine($@"              ""resource"": ""{resource}"",");
            stringBuilder.AppendLine($@"              ""path"": ""/{path}"",");
            stringBuilder.AppendLine(@"              ""queryStringParameters"": {");
            stringBuilder.AppendLine(@"                #foreach($param in $input.params().querystring.keySet())");
            stringBuilder.AppendLine(@"                ""$param"": ""$util.escapeJavaScript($input.params().querystring.get($param))"" #if($foreach.hasNext),#end");
            stringBuilder.AppendLine(@"");
            stringBuilder.AppendLine(@"                #end");
            stringBuilder.AppendLine(@"              },");
            stringBuilder.AppendLine(@"              ""pathParameters"": {");
            stringBuilder.AppendLine(@"                #foreach($param in $input.params().path.keySet())");
            stringBuilder.AppendLine(@"                ""$param"": ""$util.escapeJavaScript($input.params().path.get($param))"" #if($foreach.hasNext),#end");
            stringBuilder.AppendLine(@"");
            stringBuilder.AppendLine(@"                #end");
            stringBuilder.AppendLine(@"              },");
            stringBuilder.AppendLine(@"              ""headers"": {");
            BuildRequestHeaders(stringBuilder);
            stringBuilder.AppendLine(@"              },");
            stringBuilder.AppendLine(@"              ""requestContext"": {");
            stringBuilder.AppendLine(@"                ""domainName"": ""$context.domainName""");
            stringBuilder.AppendLine(@"              },");
            stringBuilder.AppendLine(@"              ""body"": ""$util.escapeJavaScript($input.json('$'))""");
            stringBuilder.AppendLine(@"            }");
            stringBuilder.AppendLine(@"        responses:");
            stringBuilder.AppendLine(@"          default:");
            stringBuilder.AppendLine(@"            statusCode: ""200""");
            stringBuilder.AppendLine(@"            responseParameters:");
            stringBuilder.AppendLine(@$"              method.response.header.Access-Control-Allow-Methods: ""'{verb},OPTIONS'""");
            stringBuilder.AppendLine(@$"              method.response.header.Access-Control-Allow-Headers: ""'{_options.AllowedHeaders}'""");
            stringBuilder.AppendLine(@"              method.response.header.Cache-Control: ""'no-store, no-cache'""");
            stringBuilder.AppendLine(@"              method.response.header.Content-Security-Policy: ""'default-src \\'none\\';'""");
            stringBuilder.AppendLine(@"              method.response.header.Referrer-Policy: ""'no-referrer'""");
            stringBuilder.AppendLine(@"              method.response.header.Strict-Transport-Security: ""'max-age=31536000'""");
            stringBuilder.AppendLine(@"              method.response.header.X-Content-Type-Options: ""'nosniff'""");
            stringBuilder.AppendLine(@"              method.response.header.X-Frame-Options: ""'deny'""");
            stringBuilder.AppendLine(@"              method.response.header.X-XSS-Protection: ""'1; mode=block'""");
            stringBuilder.AppendLine(@"            responseTemplates:");
            stringBuilder.AppendLine(@"              application/json: |");
            stringBuilder.AppendLine(@"                #set($context.responseOverride.status = $input.path('$.statusCode'))");
            stringBuilder.AppendLine(@"                $input.path('$.body')");
            stringBuilder.AppendLine(@"");
            CorsHeaders(stringBuilder);

            stringBuilder.AppendLine(@"");
            return stringBuilder.ToString();
        }

        // The always-present transport headers plus any configured identity headers, emitted as a JSON
        // object body with correct comma placement (no trailing comma on the last entry).
        private void BuildRequestHeaders(StringBuilder stringBuilder)
        {
            var entries = new List<string>
            {
                @"""Content-Type"": ""application/json""",
                @"""CorrelationId"": ""$context.requestId""",
                @"""SourceIP"": ""$context.identity.sourceIp""",
                @"""UserAgent"": ""$context.identity.userAgent""",
            };

            entries.AddRange(_options.IdentityHeaders.Select(header => $@"""{header.Key}"":""{header.Value}"""));

            for (var i = 0; i < entries.Count; i++)
            {
                var comma = i < entries.Count - 1 ? "," : string.Empty;
                stringBuilder.AppendLine($"{HeaderIndent}{entries[i]}{comma}");
            }
        }

        private static void AddResponses(StringBuilder stringBuilder)
        {
            var responsesDictionary = new Dictionary<string, string>
            {
                { "200", "200OkEmpty" },
                { "201", "201Created" },
                { "204", "204NoContent" },
                { "400", "400BadRequest" },
                { "401", "401Unauthorised" },
                { "403", "403Forbidden" },
                { "404", "404NotFound" },
                { "422", "422UnprocessableEntity" },
                { "500", "500InternalServerError" },
                { "503", "503ServiceUnavailable" }
            };

            stringBuilder.AppendLine(@"      responses:");

            foreach (var response in responsesDictionary)
            {
                stringBuilder.AppendLine(@$"        ""{response.Key}"":");
                stringBuilder.AppendLine(@$"          $ref: ""#/components/responses/{response.Value}""");
            }
        }

        private static void BuildParameters(RouteTemplate routeTemplate, StringBuilder stringBuilder)
        {
            if (routeTemplate.Parameters.Any())
            {
                stringBuilder.AppendLine(@"      parameters:");
                foreach (var parameter in routeTemplate.Parameters)
                {
                    stringBuilder.AppendLine($@"      - name: {parameter.Name}");
                    stringBuilder.AppendLine(@"        in: path");
                    stringBuilder.AppendLine(@"        required: true");
                    stringBuilder.AppendLine(@"        schema:");
                    stringBuilder.AppendLine(@"          type: string");
                }
            }
        }

        private string CreateTag(string path)
        {
            var parts = path
                .Split('/')
                .Where(x => !x.StartsWith("{"))
                .Select(x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x))
                .ToArray();

            return string.Join(' ', parts);
        }

    }
}
