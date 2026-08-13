using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Benzene.CodeGen.SourceGenerators
{
    [Generator]
    public class MessageHandlerSourceGenerator : IIncrementalGenerator
    {
        public static readonly DiagnosticDescriptor DuplicateTopic = new(
            id: "BENZ001",
            title: "Duplicate message topic",
            messageFormat: "Message topic '{0}'{1} is handled by multiple handlers: {2}",
            category: "Benzene",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A handler with an HTTP route and no topic. Handler discovery keys on <c>[Message]</c>, so
        /// such a handler is skipped entirely and its route never exists — a 404 at runtime with
        /// nothing to say why.
        /// </summary>
        /// <remarks>
        /// <c>UnroutedHttpEndpointCheck</c> in Benzene.Http catches the same mistake, but only when the
        /// route table is first built, which is the first request. The rule is purely syntactic, so
        /// the compiler can say it before the code is ever run. The runtime check stays as the
        /// belt-and-braces path for handlers registered explicitly, which an analyzer cannot see.
        /// </remarks>
        public static readonly DiagnosticDescriptor HttpEndpointWithoutMessage = new(
            id: "BENZ002",
            title: "[HttpEndpoint] handler has no topic",
            messageFormat: "'{0}' has [HttpEndpoint] but no [Message] attribute, so handler discovery skips it and its HTTP route will not exist. Add [Message(\"topic\")], or register the handler explicitly with a topic.",
            category: "Benzene",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A handler registered on one of Benzene's own reserved topic ids (Benzene.Abstractions'
        /// <c>BenzeneTopic</c> — kept in sync by hand, the same way the interface/attribute full
        /// names below are: an analyzer project doesn't take a runtime dependency on the library it
        /// inspects). These specific ids are always intercepted by dedicated framework middleware
        /// (health checks, the mesh descriptor endpoint, ...), never served by an ordinary
        /// <c>[Message]</c> handler — so a handler declaring one is either unreachable dead code (the
        /// interception wins) or, if that interception is ever removed from the pipeline, silently
        /// answering as the framework's own endpoint. Deliberately narrower than "any benzene:-prefixed
        /// topic": the wider <c>benzene:mesh:*</c> namespace is legitimately extended by a mesh
        /// collector implementing mesh.md §4's ingest topics as ordinary handlers (see
        /// examples/AwsMesh/Mesh/MeshAggregateHandler.cs's <c>benzene:mesh:aggregate</c>), which this
        /// rule must not flag.
        /// </summary>
        public static readonly DiagnosticDescriptor ReservedTopicHandler = new(
            id: "BENZ003",
            title: "Handler registered on a reserved Benzene topic",
            messageFormat: "'{0}' is registered on '{1}', one of Benzene's own reserved topic ids. This topic is always intercepted by dedicated framework middleware before handler dispatch, so this handler can never run (or, if that interception is ever disabled, will answer in place of the framework's own endpoint). Choose an application topic id instead.",
            category: "Benzene",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A handler's request or response type is one that <c>Benzene.Mesh.Wire.MeshSchemaGenerator</c>
        /// derives an unconstrained (<c>{}</c>) JSON Schema for — checked at the top level only (the
        /// exact same construct list the generator special-cases before it would otherwise walk
        /// properties, per docs/specification/mesh.md §2.1): <c>object</c>, <c>dynamic</c>, an enum,
        /// or a <c>System.Text.Json</c> dynamic-document type. Not an error: an unconstrained schema is
        /// legal and sometimes deliberate (a genuinely dynamic payload), but it means this topic's
        /// mesh descriptor and derived spec carry no shape for the flagged side, so any generated
        /// client, mesh UI schema view, or OpenAPI/AsyncAPI document for it degrades to "any JSON" —
        /// worth an explicit choice, not a surprise found by whoever reads the fleet view later.
        /// </summary>
        public static readonly DiagnosticDescriptor UnconstrainedSchemaType = new(
            id: "BENZ004",
            title: "Handler payload type publishes an unconstrained mesh schema",
            messageFormat: "'{0}' is the {1} type of handler '{2}'; its mesh descriptor and derived spec will publish an unconstrained ('{{}}') schema for it — {1}s of this shape carry no discoverable contract for generated clients, the mesh UI, or OpenAPI/AsyncAPI. Prefer a concrete type over '{0}' if one exists; otherwise this is fine as a deliberate escape hatch.",
            category: "Benzene",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        /// <summary>
        /// Benzene.Abstractions.BenzeneTopic.All, hand-copied (see <see cref="ReservedTopicHandler"/>'s
        /// doc comment for why an analyzer can't just reference it). Update alongside that type.
        /// </summary>
        private static readonly ImmutableHashSet<string> KnownReservedTopicIds = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "benzene:spec",
            "benzene:test-payloads",
            "benzene:healthcheck",
            "benzene:liveness",
            "benzene:readiness",
            "benzene:mesh",
            "benzene:ping");

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: (ctx, _) => GetMessageHandlerInfo(ctx))
                .Where(t => t is not null);

            var unrouted = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: (ctx, _) => GetUnroutedHttpEndpoint(ctx))
                .Where(t => t is not null);

            // Reported per handler rather than off a Collect(), so one handler gaining or losing an
            // attribute doesn't invalidate the diagnostic for every other handler in the compilation.
            context.RegisterSourceOutput(unrouted, (spc, handler) => spc.ReportDiagnostic(
                Diagnostic.Create(HttpEndpointWithoutMessage, handler!.Location, handler.HandlerFullType)));

            // BENZ003/BENZ004 are per-handler, single-item checks (unlike BENZ001's cross-handler
            // duplicate-topic check below) - reported off the same pre-Collect() provider as `unrouted`,
            // for the same incremental-caching reason.
            context.RegisterSourceOutput(provider, (spc, handler) => ReportValidationDiagnostics(spc, handler!));

            // Drive the output straight off the collected handlers. Combining with CompilationProvider
            // (as this used to) re-runs the whole output on every compilation - i.e. every keystroke -
            // defeating the incremental generator's caching, and the Compilation was never even used by
            // Execute. With the handler model now a value-equality record, an unchanged set of handlers
            // produces an equal collected array, so RegisterSourceOutput's cache short-circuits.
            context.RegisterSourceOutput(provider.Collect(), (spc, handlers) => Execute(spc, handlers!));
        }

        private static UnroutedHttpEndpointInfo? GetUnroutedHttpEndpoint(GeneratorSyntaxContext context)
        {
            var classDeclaration = (ClassDeclarationSyntax)context.Node;

            if (context.SemanticModel.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol typeSymbol
                || typeSymbol.IsAbstract)
            {
                return null;
            }

            var attributes = typeSymbol.GetAttributes();

            if (!attributes.Any(IsAttributeNamed("HttpEndpoint")) || attributes.Any(IsAttributeNamed("Message")))
            {
                return null;
            }

            // Only a message handler is affected: an [HttpEndpoint] on anything else is not something
            // Benzene's discovery was ever going to route.
            var isHandler = typeSymbol.AllInterfaces.Any(i =>
                i.IsGenericType && (
                    i.ConstructedFrom.ToString() == "Benzene.Abstractions.MessageHandlers.IMessageHandler<TRequest, TResponse>" ||
                    i.ConstructedFrom.ToString() == "Benzene.Abstractions.MessageHandlers.IMessageHandler<TRequest>"));

            return isHandler
                ? new UnroutedHttpEndpointInfo(typeSymbol.ToDisplayString(), classDeclaration.Identifier.GetLocation())
                : null;
        }

        private static Func<AttributeData, bool> IsAttributeNamed(string name) =>
            attribute => attribute.AttributeClass?.Name == name || attribute.AttributeClass?.Name == name + "Attribute";

        private static MessageHandlerInfo? GetMessageHandlerInfo(GeneratorSyntaxContext context)
        {
            var classDeclaration = (ClassDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);

            if (symbol is not INamedTypeSymbol typeSymbol)
                return null;

            var messageAttribute = typeSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Benzene.Core.MessageHandlers.MessageAttribute" || 
                                     a.AttributeClass?.Name == "MessageAttribute" || 
                                     a.AttributeClass?.Name == "Message");

            if (messageAttribute == null)
                return null;

            var interfaces = typeSymbol.AllInterfaces;
            var handlerInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType && (
                    i.ConstructedFrom.ToString() == "Benzene.Abstractions.MessageHandlers.IMessageHandler<TRequest, TResponse>" ||
                    i.ConstructedFrom.ToString() == "Benzene.Abstractions.MessageHandlers.IMessageHandler<TRequest>"));

            if (handlerInterface == null)
                return null;

            var topic = "";
            var version = "";

            if (messageAttribute.ConstructorArguments.Length > 0)
            {
                topic = messageAttribute.ConstructorArguments[0].Value?.ToString() ?? "";
            }
            else
            {
                var topicArg = messageAttribute.NamedArguments.FirstOrDefault(a => a.Key == "Topic");
                if (topicArg.Key != null)
                {
                    topic = topicArg.Value.Value?.ToString() ?? "";
                }
            }

            if (messageAttribute.ConstructorArguments.Length > 1)
            {
                version = messageAttribute.ConstructorArguments[1].Value?.ToString() ?? "";
            }
            else
            {
                var versionArg = messageAttribute.NamedArguments.FirstOrDefault(a => a.Key == "Version");
                if (versionArg.Key != null)
                {
                    version = versionArg.Value.Value?.ToString() ?? "";
                }
            }

            // Fully qualified, global:: and all. The generated file sits in namespace
            // Benzene.Core.MessageHandlers.DI with Benzene usings in scope, so an unqualified name can
            // be captured by something else that is visible from there - a request type called Request
            // in the global namespace resolved to the Benzene.Core.MessageHandlers.Request *namespace*
            // and the generated file stopped compiling.
            var requestTypeSymbol = handlerInterface.TypeArguments[0];
            var requestType = requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            // TypeArguments.Length == 1 means IMessageHandler<TRequest> (no declared response type,
            // Void) - deliberately not checked for an unconstrained schema; Void isn't a payload.
            var hasResponseType = handlerInterface.TypeArguments.Length > 1;
            var responseTypeSymbol = hasResponseType ? handlerInterface.TypeArguments[1] : null;
            var responseType = hasResponseType
                ? responseTypeSymbol!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : "global::Benzene.Abstractions.Results.Void";

            return new MessageHandlerInfo(
                topic,
                version,
                requestType,
                responseType,
                typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                classDeclaration.Identifier.GetLocation(),
                isReservedTopicHandler: KnownReservedTopicIds.Contains(topic),
                requestIsUnconstrainedSchema: IsUnconstrainedSchemaType(requestTypeSymbol),
                responseIsUnconstrainedSchema: hasResponseType && IsUnconstrainedSchemaType(responseTypeSymbol!)
            );
        }

        /// <summary>
        /// Mirrors the top-level special-cases of <c>Benzene.Mesh.Wire.MeshSchemaGenerator.SchemaFor</c>
        /// (the reflection-based schema deriver, over <see cref="System.Type"/>) using Roslyn symbols
        /// instead - the same "an analyzer inspects via symbols, not by referencing the runtime type"
        /// split as everywhere else in this file. Deliberately shallow: it does not walk into object
        /// properties or detect recursive cycles the way the runtime deriver does, so it only catches a
        /// handler's own top-level request/response type being one of these constructs directly, not
        /// one nested inside a property. That is the sound subset - going deeper risks false positives
        /// this analyzer cannot cheaply rule out (e.g. a property genuinely meant to be dynamic).
        /// </summary>
        private static bool IsUnconstrainedSchemaType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic || type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            // The plain (non-fully-qualified) display string for a type outside the current
            // compilation's usings is already "System.Text.Json.JsonElement" with no "global::"
            // prefix to strip, unlike the FullyQualifiedFormat used elsewhere in this file for
            // generated-code emission.
            var fullName = type.ToDisplayString();
            if (fullName == "System.Text.Json.JsonElement" || fullName == "System.Text.Json.JsonDocument")
            {
                return true;
            }

            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (baseType.ToDisplayString() == "System.Text.Json.Nodes.JsonNode")
                {
                    return true;
                }
            }

            return false;
        }

        private static void ReportValidationDiagnostics(SourceProductionContext context, MessageHandlerInfo handler)
        {
            var handlerName = handler.HandlerFullType.Replace("global::", "");

            if (handler.IsReservedTopicHandler)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ReservedTopicHandler, handler.Location, handlerName, handler.Topic));
            }

            if (handler.RequestIsUnconstrainedSchema)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnconstrainedSchemaType, handler.Location, handler.RequestFullType.Replace("global::", ""), "request", handlerName));
            }
            if (handler.ResponseIsUnconstrainedSchema)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnconstrainedSchemaType, handler.Location, handler.ResponseFullType.Replace("global::", ""), "response", handlerName));
            }
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<MessageHandlerInfo> handlers)
        {
            if (handlers.IsDefaultOrEmpty)
                return;

            var duplicateGroups = handlers
                .GroupBy(h => new { h.Topic, h.Version })
                .Where(g => g.Count() > 1);

            foreach (var group in duplicateGroups)
            {
                var versionText = string.IsNullOrEmpty(group.Key.Version) ? "" : $" (version '{group.Key.Version}')";
                // Names are stored fully qualified for codegen; "global::" is noise in a message a
                // developer reads.
                var handlerNames = string.Join(", ", group.Select(h => h.HandlerFullType.Replace("global::", "")));
                foreach (var handler in group)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateTopic, handler.Location, group.Key.Topic, versionText, handlerNames));
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("using Benzene.Abstractions.DI;");
            sb.AppendLine("using Benzene.Abstractions.MessageHandlers;");
            sb.AppendLine("using Benzene.Core.MessageHandlers;");
            sb.AppendLine("");
            sb.AppendLine("namespace Benzene.Core.MessageHandlers.DI");
            sb.AppendLine("{");
            // internal, not public. The generator now runs in every assembly that references
            // Benzene.Core.MessageHandlers, so a solution with two handler-carrying projects — one
            // referencing the other — would otherwise end up with two public
            // BenzeneGeneratedHandlersExtensions in the same namespace and an ambiguous call at the
            // composition root. Internal gives each assembly its own copy, which is the right scope
            // anyway: the generated registrations only describe that assembly's handlers.
            sb.AppendLine("    internal static class BenzeneGeneratedHandlersExtensions");
            sb.AppendLine("    {");
            sb.AppendLine("        internal static IBenzeneServiceContainer AddGeneratedMessageHandlers(this IBenzeneServiceContainer services)");
            sb.AppendLine("        {");
            // Register the dispatch infrastructure through the ordinary no-reflection overload, then
            // hand each handler over as an IMessageHandlerDefinition — the seam
            // DependencyMessageHandlersFinder already reads, and the same one a library shipping its
            // own handlers uses.
            //
            // This used to open with `services.GetService<MessageHandlersList>()`, which
            // IBenzeneServiceContainer does not have (it registers services, it does not resolve
            // them), so the generated file did not compile. Nothing caught it because nothing
            // referenced the analyzer; wiring it into Benzene.Core.MessageHandlers turned it into a
            // build break in every example on the first try.
            sb.AppendLine("            services.AddMessageHandlers();");
            sb.AppendLine("");

            foreach (var handler in handlers)
            {
                // Topic/Version come from a user-authored [Message] attribute, so they can contain a
                // quote, backslash or other character that would break (or inject into) the generated
                // string literal. FormatLiteral produces a correctly-escaped, quoted C# literal.
                var topicLiteral = SymbolDisplay.FormatLiteral(handler.Topic, quote: true);
                var versionLiteral = SymbolDisplay.FormatLiteral(handler.Version, quote: true);
                sb.AppendLine($"            services.AddScoped<{handler.HandlerFullType}>();");
                sb.AppendLine($"            services.AddSingleton<IMessageHandlerDefinition>(_ => MessageHandlerDefinition.CreateInstance({topicLiteral}, {versionLiteral}, typeof({handler.RequestFullType}), typeof({handler.ResponseFullType}), typeof({handler.HandlerFullType})));");
            }

            sb.AppendLine("");
            sb.AppendLine("            return services;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource("BenzeneGeneratedHandlers.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    // Value equality, same reason as MessageHandlerInfo below: without it every syntax pass produces a
    // "different" instance and the diagnostic is recomputed on every keystroke.
    internal sealed class UnroutedHttpEndpointInfo : IEquatable<UnroutedHttpEndpointInfo>
    {
        public UnroutedHttpEndpointInfo(string handlerFullType, Location location)
        {
            HandlerFullType = handlerFullType;
            Location = location;
        }

        public string HandlerFullType { get; }

        public Location Location { get; }

        public bool Equals(UnroutedHttpEndpointInfo? other) =>
            other is not null && HandlerFullType == other.HandlerFullType && Location.Equals(other.Location);

        public override bool Equals(object? obj) => Equals(obj as UnroutedHttpEndpointInfo);

        public override int GetHashCode()
        {
            unchecked
            {
                return (HandlerFullType.GetHashCode() * 31) + Location.GetHashCode();
            }
        }
    }

    // Value equality is what lets the incremental pipeline cache: RegisterSourceOutput re-runs only
    // when the collected array of these actually differs. As a plain reference-equality class every
    // syntax pass produced "different" instances and the generator re-ran every time. Location is
    // included so a duplicate diagnostic still moves with the code, at the cost of a cache miss when a
    // handler's line shifts - still far better than never caching.
    internal sealed class MessageHandlerInfo : IEquatable<MessageHandlerInfo>
    {
        public string Topic { get; }
        public string Version { get; }
        public string RequestFullType { get; }
        public string ResponseFullType { get; }
        public string HandlerFullType { get; }
        public Location Location { get; }
        public bool IsReservedTopicHandler { get; }
        public bool RequestIsUnconstrainedSchema { get; }
        public bool ResponseIsUnconstrainedSchema { get; }

        public MessageHandlerInfo(
            string topic,
            string version,
            string requestFullType,
            string responseFullType,
            string handlerFullType,
            Location location,
            bool isReservedTopicHandler,
            bool requestIsUnconstrainedSchema,
            bool responseIsUnconstrainedSchema)
        {
            Topic = topic;
            Version = version;
            RequestFullType = requestFullType;
            ResponseFullType = responseFullType;
            HandlerFullType = handlerFullType;
            Location = location;
            IsReservedTopicHandler = isReservedTopicHandler;
            RequestIsUnconstrainedSchema = requestIsUnconstrainedSchema;
            ResponseIsUnconstrainedSchema = responseIsUnconstrainedSchema;
        }

        public bool Equals(MessageHandlerInfo? other)
        {
            return other is not null
                && Topic == other.Topic
                && Version == other.Version
                && RequestFullType == other.RequestFullType
                && ResponseFullType == other.ResponseFullType
                && HandlerFullType == other.HandlerFullType
                && Location.Equals(other.Location)
                && IsReservedTopicHandler == other.IsReservedTopicHandler
                && RequestIsUnconstrainedSchema == other.RequestIsUnconstrainedSchema
                && ResponseIsUnconstrainedSchema == other.ResponseIsUnconstrainedSchema;
        }

        public override bool Equals(object? obj) => Equals(obj as MessageHandlerInfo);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (Topic?.GetHashCode() ?? 0);
                hash = hash * 31 + (Version?.GetHashCode() ?? 0);
                hash = hash * 31 + (RequestFullType?.GetHashCode() ?? 0);
                hash = hash * 31 + (ResponseFullType?.GetHashCode() ?? 0);
                hash = hash * 31 + (HandlerFullType?.GetHashCode() ?? 0);
                hash = hash * 31 + (Location?.GetHashCode() ?? 0);
                hash = hash * 31 + IsReservedTopicHandler.GetHashCode();
                hash = hash * 31 + RequestIsUnconstrainedSchema.GetHashCode();
                hash = hash * 31 + ResponseIsUnconstrainedSchema.GetHashCode();
                return hash;
            }
        }
    }
}
