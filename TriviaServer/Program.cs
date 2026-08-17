using StackExchange.Redis;
using TriviaServer;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

// Redis - the NoSQL half of the stack, holding player identity + Elo.
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException(
        "Connection string 'Redis' is missing. Set it via user-secrets or an environment variable, see SETUP.md.");
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<PlayerProfileStore>();

// Postgres - questions and in-progress match sessions.
builder.Services.AddScoped<DatabaseManager>();

// Unity WebGL builds call this API from a browser context, so it needs
// CORS enabled. Standalone/mobile builds don't need it but it's harmless
// to leave on. Lock this down to your actual game's origin before you
// ship publicly if you want to be strict about it.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUnityClient", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowUnityClient");
app.UseAuthorization();
app.MapControllers();

app.Run();