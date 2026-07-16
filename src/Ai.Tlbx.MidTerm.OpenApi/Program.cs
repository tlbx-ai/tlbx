using System.Reflection;
using Ai.Tlbx.MidTerm.Api;
using Ai.Tlbx.MidTerm.Api.Endpoints;
using Ai.Tlbx.MidTerm.Api.Handlers;
using Ai.Tlbx.MidTerm.OpenApi.Stubs;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAuthHandler, StubAuthHandler>();
builder.Services.AddSingleton<ISecurityHandler, StubSecurityHandler>();
builder.Services.AddSingleton<ISessionHandler, StubSessionHandler>();
builder.Services.AddSingleton<IBrowserHandler, StubBrowserHandler>();
builder.Services.AddSingleton<IWebPreviewHandler, StubWebPreviewHandler>();
builder.Services.AddSingleton<IPowerHandler, StubPowerHandler>();
builder.Services.AddSingleton<IHistoryHandler, StubHistoryHandler>();
builder.Services.AddSingleton<IFileHandler, StubFileHandler>();
builder.Services.AddSingleton<ILogHandler, StubLogHandler>();
builder.Services.AddSingleton<IShareHandler, StubShareHandler>();
builder.Services.AddSingleton<ISystemHandler, StubSystemHandler>();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info.Title = "tlbx API";
        document.Info.Version = "1.0.0";
        document.Info.Description = "tlbx browser control station API";
        return Task.CompletedTask;
    });

    options.AddSchemaTransformer((schema, context, ct) =>
    {
        FixNumericUnionType(schema);

        if (schema.Properties != null)
        {
            foreach (var prop in schema.Properties.Values)
            {
                if (prop is OpenApiSchema propSchema)
                {
                    FixNumericUnionType(propSchema);
                }
            }
        }

        var clrType = context.JsonTypeInfo?.Type;
        if (clrType != null && schema.Properties?.Count > 0)
        {
            var nullabilityContext = new NullabilityInfoContext();
            var isRequestDto = clrType.Name.EndsWith("Request") || clrType.Name.EndsWith("Payload");

            foreach (var clrProp in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var camelName = char.ToLowerInvariant(clrProp.Name[0]) + clrProp.Name[1..];
                if (!schema.Properties.ContainsKey(camelName)) continue;

                var propType = clrProp.PropertyType;
                bool isNullable;

                if (propType.IsValueType)
                {
                    isNullable = Nullable.GetUnderlyingType(propType) != null;
                }
                else
                {
                    var nullInfo = nullabilityContext.Create(clrProp);
                    isNullable = nullInfo.WriteState != NullabilityState.NotNull;
                }

                if (!isNullable || !isRequestDto)
                {
                    schema.Required ??= new HashSet<string>();
                    schema.Required.Add(camelName);
                }
            }
        }

        return Task.CompletedTask;
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

app.MapOpenApi();
app.MapAllApiEndpoints();

app.Run();

static void FixNumericUnionType(OpenApiSchema schema)
{
    if (!schema.Type.HasValue) return;

    if (schema.Type.Value.HasFlag(JsonSchemaType.Integer) &&
        schema.Type.Value.HasFlag(JsonSchemaType.String))
    {
        schema.Type = schema.Type.Value & ~JsonSchemaType.String;
        schema.Pattern = null;
    }

    if (schema.Type.Value.HasFlag(JsonSchemaType.Number) &&
        schema.Type.Value.HasFlag(JsonSchemaType.String))
    {
        schema.Type = schema.Type.Value & ~JsonSchemaType.String;
        schema.Pattern = null;
    }
}
