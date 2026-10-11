using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.DataAnnotations;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Benzene.Test.Plugins.DataAnnotations;

// wire-contracts §1.3 (D8): DataAnnotations member names reach the wire as JSON Pointers into the
// request's JSON form, and a class-level rule (no member) omits field.
public class DataAnnotationsFieldPointerTest
{
    public class AnnotatedRequest : IValidatableObject
    {
        [Required]
        public string? CustomerName { get; set; }

        [Required]
        [JsonPropertyName("ref")]
        public string? Reference { get; set; }

        public List<AnnotatedLine> Lines { get; set; } = new();

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            for (var i = 0; i < Lines.Count; i++)
            {
                if (string.IsNullOrEmpty(Lines[i].Sku))
                {
                    // A nested path reported the host-language way.
                    yield return new ValidationResult("Sku is required", new[] { $"Lines[{i}].Sku" });
                }
            }

            if (Lines.Count > 2)
            {
                yield return new ValidationResult("Too many lines");
            }
        }
    }

    public class AnnotatedLine
    {
        public string? Sku { get; set; }
    }

    public class AnnotatedHandler : IMessageHandler<AnnotatedRequest, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(AnnotatedRequest request)
        {
            return Task.FromResult(BenzeneResult.Ok("ok"));
        }
    }

    [Fact]
    public async Task MemberNames_ArePointersIntoTheJsonForm()
    {
        var fields = await RunAsync("{\"customerName\":null,\"ref\":null,\"lines\":[]}");

        Assert.Equal(new[] { "/customerName", "/ref" }, fields.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task NestedPaths_AndClassLevelRules()
    {
        var fields = await RunAsync("{\"customerName\":\"c\",\"ref\":\"r\",\"lines\":[{\"sku\":\"a\"},{},{}]}");

        Assert.Equal(new[] { "/lines/1/sku", "/lines/2/sku", null }, fields);
    }

    private static async Task<string?[]> RunAsync(string body)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        container.AddBenzene().AddBenzeneMessage();
        container.AddScoped<AnnotatedHandler>();

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipeline.UseMessageHandlers(Array.Empty<Type>(), x => x
            .UseDataAnnotationsValidation()
            .AddMessageHandler<AnnotatedHandler, AnnotatedRequest, string>("annotated:order"));

        var application = new BenzeneMessageApplication(pipeline.Build());
        var response = await application.HandleAsync(
            new BenzeneMessageRequest { Topic = "annotated:order", Body = body },
            container.CreateServiceResolverFactory());

        Assert.Equal(BenzeneResultStatus.ValidationError, response.StatusCode);
        using var problem = JsonDocument.Parse(response.Body);
        return problem.RootElement.GetProperty("errors").EnumerateArray()
            .Select(x => x.TryGetProperty("field", out var field) ? field.GetString() : null)
            .ToArray();
    }
}
