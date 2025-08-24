using Microsoft.AspNetCore.Mvc;
using Discord.WebSocket;
using Onboarding_bot.Services;
using Onboarding_bot.Handlers;
using Discord;

namespace Onboarding_bot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class TestController : ControllerBase
    {
        private readonly DiscordSocketClient _client;
        private readonly OnboardingService _onboardingService;
        private readonly StoryService _storyService;
        private readonly OnboardingHandler _onboardingHandler;

        public TestController(
            DiscordSocketClient client,
            OnboardingService onboardingService,
            StoryService storyService,
            OnboardingHandler onboardingHandler)
        {
            _client = client;
            _onboardingService = onboardingService;
            _storyService = storyService;
            _onboardingHandler = onboardingHandler;
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                BotStatus = new
                {
                    ConnectionState = _client.ConnectionState.ToString(),
                    IsConnected = _client.ConnectionState == ConnectionState.Connected,
                    BotName = _client.CurrentUser?.Username ?? "Unknown",
                    BotId = _client.CurrentUser?.Id ?? 0,
                    GuildsCount = _client.Guilds.Count,
                    Latency = _client.Latency
                },
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

        [HttpGet("guilds")]
        public IActionResult GetGuilds()
        {
            if (_client.ConnectionState != ConnectionState.Connected)
            {
                return BadRequest("Bot is not connected to Discord");
            }

            var guilds = _client.Guilds.Select(g => new
            {
                Id = g.Id,
                Name = g.Name,
                MemberCount = g.MemberCount,
                Owner = g.Owner?.Username ?? "Unknown",
                Channels = g.Channels.Select(c => new
                {
                    Id = c.Id,
                    Name = c.Name,
                    Type = c.GetType().Name
                }).Take(5).ToList() // أول 5 قنوات فقط
            }).ToList();

            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                GuildsCount = guilds.Count,
                Guilds = guilds
            });
        }

        [HttpGet("channels/{guildId}")]
        public IActionResult GetChannels(ulong guildId)
        {
            if (_client.ConnectionState != ConnectionState.Connected)
            {
                return BadRequest("Bot is not connected to Discord");
            }

            var guild = _client.GetGuild(guildId);
            if (guild == null)
            {
                return NotFound($"Guild with ID {guildId} not found");
            }

            var channels = guild.Channels.Select(c => new
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.GetType().Name,
                Position = c.Position
            }).OrderBy(c => c.Position).ToList();

            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                GuildName = guild.Name,
                GuildId = guild.Id,
                ChannelsCount = channels.Count,
                Channels = channels
            });
        }

        [HttpGet("roles/{guildId}")]
        public IActionResult GetRoles(ulong guildId)
        {
            if (_client.ConnectionState != ConnectionState.Connected)
            {
                return BadRequest("Bot is not connected to Discord");
            }

            var guild = _client.GetGuild(guildId);
            if (guild == null)
            {
                return NotFound($"Guild with ID {guildId} not found");
            }

            var roles = guild.Roles.Select(r => new
            {
                Id = r.Id,
                Name = r.Name,
                Color = r.Color.ToString(),
                Position = r.Position,
                IsManaged = r.IsManaged,
                IsHoisted = r.IsHoisted,
                Permissions = r.Permissions.ToString()
            }).OrderByDescending(r => r.Position).ToList();

            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                GuildName = guild.Name,
                GuildId = guild.Id,
                RolesCount = roles.Count,
                Roles = roles
            });
        }

        [HttpGet("test-story")]
        public async Task<IActionResult> TestStoryGeneration()
        {
            try
            {
                // اختبار إنشاء قصة
                var testResponses = new Dictionary<string, string>
                {
                    ["name"] = "أحمد محمد",
                    ["age"] = "25 سنة",
                    ["interest"] = "أتعلم البرمجة وأتعرف على ناس جدد",
                    ["specialty"] = "تطوير الويب",
                    ["strength"] = "الصبر والتعلم المستمر",
                    ["weakness"] = "أحياناً أكون مثالي أكثر من اللازم",
                    ["favoritePlace"] = "المكتبة العامة"
                };

                var story = await _storyService.GenerateStoryAsync(
                    testResponses, 
                    "محمد علي", 
                    "مطور", 
                    "قصة قديمة عن المطور محمد"
                );

                return Ok(new
                {
                    Timestamp = DateTime.UtcNow,
                    TestResponses = testResponses,
                    GeneratedStory = story,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Timestamp = DateTime.UtcNow,
                    Error = ex.Message,
                    StackTrace = ex.StackTrace,
                    Success = false
                });
            }
        }

        [HttpGet("test-embed")]
        public IActionResult TestEmbed()
        {
            try
            {
                var embed = new EmbedBuilder()
                    .WithTitle("🧪 اختبار البوت")
                    .WithDescription("هذا اختبار لإنشاء Embeds")
                    .WithColor(Color.Blue)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .WithFooter(footer => footer.Text = "اختبار من Test Controller")
                    .Build();

                return Ok(new
                {
                    Timestamp = DateTime.UtcNow,
                    EmbedTitle = embed.Title,
                    EmbedDescription = embed.Description,
                    EmbedColor = embed.Color?.ToString(),
                    EmbedTimestamp = embed.Timestamp,
                    EmbedFooter = embed.Footer?.Text,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Timestamp = DateTime.UtcNow,
                    Error = ex.Message,
                    Success = false
                });
            }
        }

        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new
            {
                Message = "Pong!",
                Timestamp = DateTime.UtcNow,
                BotStatus = _client.ConnectionState.ToString()
            });
        }
    }
}
