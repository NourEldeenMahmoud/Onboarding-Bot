using Microsoft.AspNetCore.Mvc;
using Discord.WebSocket;

namespace Onboarding_bot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly DiscordSocketClient _client;

        public HealthController(DiscordSocketClient client)
        {
            _client = client;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new
            {
                Status = "Running",
                BotConnected = _client.ConnectionState == Discord.ConnectionState.Connected,
                BotName = _client.CurrentUser?.Username ?? "Unknown",
                GuildsCount = _client.Guilds.Count,
                EnvironmentVariables = new
                {
                    HasDiscordToken = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISCORD_TOKEN")),
                    HasOpenAIKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_KEY")),
                    HasStoryChannel = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID")),
                    HasCityGates = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISCORD_CITY_GATES_CHANNEL_ID")),
                    HasOutsiderRole = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISCORD_OUTSIDER_ROLE_ID")),
                    HasAssociateRole = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISCORD_ASSOCIATE_ROLE_ID")),
                    HasOwnerId = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OWNER_ID")),
                    HasLogChannel = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOG_CHANNEL_ID")),
                    HasPort = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORT"))
                }
            });
        }
    }
}
