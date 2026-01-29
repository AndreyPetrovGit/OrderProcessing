using OrderProcessing.DTOs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/order", (CreateOrderDto dto) =>
    {
        throw new NotImplementedException();
    })
    .WithName("PostOrder");

app.MapGet("/order", () =>
    {
        throw new NotImplementedException();
    })
    .WithName("GetOrder");

app.MapGet("/test", Guid.NewGuid) // TODO: UUID v7
    .WithName("GetGuid");

app.Run();