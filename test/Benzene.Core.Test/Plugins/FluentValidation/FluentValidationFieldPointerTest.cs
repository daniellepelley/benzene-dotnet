using System;
using System.Collections.Generic;
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
using Benzene.FluentValidation;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CoreJsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;

namespace Benzene.Test.Plugins.FluentValidation;

// wire-contracts §1.3 (D8): FluentValidation's property paths (Items[0].Sku) reach the wire as JSON
// Pointers into the request's JSON form (/items/0/sku), named by the service's own serializer.
public class FluentValidationFieldPointerTest
{
    public class PointerOrderRequest
    {
        public string? Name { get; set; }
        public List<PointerOrderLine>? LineItems { get; set; }

        [JsonPropertyName("ref")]
        public string? Reference { get; set; }

        public int Quantity { get; set; }
        public int Maximum { get; set; }
    }

    public class PointerOrderLine
    {
        public string? Sku { get; set; }
    }

    public class PointerOrderValidator : AbstractValidator<PointerOrderRequest>
    {
        public PointerOrderValidator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Reference).NotEmpty();
            RuleForEach(x => x.LineItems).ChildRules(line => line.RuleFor(x => x.Sku).NotEmpty());
            // A cross-field rule on the whole request names no single location.
            RuleFor(x => x).Must(x => x.Quantity <= x.Maximum).WithName("order").WithMessage("Quantity exceeds maximum");
        }
    }

    // The cookbook's cross-field pattern: WithName attributes a whole-request rule to one property.
    public class AttributedCrossFieldValidator : AbstractValidator<PointerOrderRequest>
    {
        public AttributedCrossFieldValidator()
        {
            RuleFor(x => x).Must(x => x.Quantity <= x.Maximum).WithName(nameof(PointerOrderRequest.Maximum));
        }
    }

    public class PointerOrderHandler : IMessageHandler<PointerOrderRequest, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(PointerOrderRequest request)
        {
            return Task.FromResult(BenzeneResult.Ok("ok"));
        }
    }

    private const string InvalidBody =
        "{\"name\":\"\",\"ref\":\"\",\"lineItems\":[{\"sku\":\"a\"},{}],\"quantity\":2,\"maximum\":1}";

    [Fact]
    public async Task ValidationFailure_FieldsArePointersIntoTheJsonForm()
    {
        var errors = await RunAsync(InvalidBody, null);

        Assert.Contains(errors, x => x.Field == "/name" && x.Code == "NotEmptyValidator");
        Assert.Contains(errors, x => x.Field == "/ref");
        Assert.Contains(errors, x => x.Field == "/lineItems/1/sku");
        Assert.Contains(errors, x => x.Field == null && x.Message == "Quantity exceeds maximum");
        Assert.Equal(4, errors.Length);
    }

    [Fact]
    public async Task ValidationFailure_PointersFollowTheConfiguredSerializerNaming()
    {
        var snakeCase = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        var errors = await RunAsync(
            "{\"name\":\"\",\"ref\":\"x\",\"line_items\":[{}],\"quantity\":0,\"maximum\":1}",
            new CoreJsonSerializer(snakeCase));

        Assert.Equal(new[] { "/line_items/0/sku", "/name" }, errors.Select(x => x.Field).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ToBenzeneErrors_ConvertsEachFailure()
    {
        var result = new PointerOrderValidator().Validate(new PointerOrderRequest
        {
            Name = "n",
            Reference = "r",
            LineItems = new List<PointerOrderLine> { new(), new() { Sku = "s" }, new() }
        });

        var errors = result.ToBenzeneErrors(typeof(PointerOrderRequest));

        Assert.Equal(new[] { "/lineItems/0/sku", "/lineItems/2/sku" }, errors.Select(x => x.Field));
        Assert.All(errors, x => Assert.Equal("NotEmptyValidator", x.Code));
    }

    [Fact]
    public void ToBenzeneErrors_CrossFieldRuleAttributedWithWithName_PointsAtThatProperty()
    {
        var result = new AttributedCrossFieldValidator().Validate(new PointerOrderRequest { Quantity = 2, Maximum = 1 });

        var error = Assert.Single(result.ToBenzeneErrors(typeof(PointerOrderRequest)));
        Assert.Equal("/maximum", error.Field);
    }

    private static async Task<BenzeneError[]> RunAsync(string body, CoreJsonSerializer? serializer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var container = new MicrosoftBenzeneServiceContainer(services);
        if (serializer != null)
        {
            // Registered before AddBenzene, whose registrations are all TryAdd - the documented way to
            // install custom JsonSerializerOptions for the JSON request path.
            container.AddSingleton(serializer);
        }

        container.AddBenzene().AddBenzeneMessage();
        container.AddScoped<PointerOrderHandler>();

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(container);
        pipeline.UseMessageHandlers(Array.Empty<Type>(), x => x
            .UseFluentValidation(new[] { typeof(PointerOrderValidator) })
            .AddMessageHandler<PointerOrderHandler, PointerOrderRequest, string>("pointer:order"));

        var application = new BenzeneMessageApplication(pipeline.Build());
        var response = await application.HandleAsync(
            new BenzeneMessageRequest { Topic = "pointer:order", Body = body },
            container.CreateServiceResolverFactory());

        Assert.Equal(BenzeneResultStatus.ValidationError, response.StatusCode);
        using var problem = JsonDocument.Parse(response.Body);
        return problem.RootElement.GetProperty("errors").EnumerateArray()
            .Select(x => new BenzeneError(
                x.GetProperty("message").GetString()!,
                x.TryGetProperty("field", out var field) ? field.GetString() : null,
                x.TryGetProperty("code", out var code) ? code.GetString() : null))
            .ToArray();
    }
}
