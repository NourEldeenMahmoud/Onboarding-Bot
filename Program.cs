using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Onboarding_bot.Services;
using Onboarding_bot.Handlers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Add Logging
builder.Services.AddLogging();

// Add Discord Socket Client first with proper configuration
builder.Services.AddSingleton<DiscordSocketClient>(provider =>
{
    return new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.All,
        LogLevel = LogSeverity.Info
    });
});

// Add Discord Bot Service
builder.Services.AddSingleton<OnboardingService>();
builder.Services.AddSingleton<StoryService>();
builder.Services.AddSingleton<OnboardingHandler>();

// Add Discord Bot Service (after other services)
builder.Services.AddSingleton<DiscordBotService>();

// Load environment variables
DotNetEnv.Env.Load();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Add root endpoint
app.MapGet("/", () => "Onboarding Bot is running! Use /health to check status.");

// Start Discord Bot
var client = app.Services.GetRequiredService<DiscordSocketClient>();
var discordService = app.Services.GetRequiredService<DiscordBotService>();
await discordService.StartAsync();

// Keep the application running
await app.RunAsync();
