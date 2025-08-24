using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Onboarding_bot.Handlers;

namespace Onboarding_bot.Services
{
    public class DiscordBotService
    {
        private readonly DiscordSocketClient _client;
        private readonly ILogger<DiscordBotService> _logger;
        private readonly OnboardingHandler _onboardingHandler;
        private readonly Dictionary<string, int> _inviteUses = new();

        public DiscordBotService(
            ILogger<DiscordBotService> logger,
            OnboardingHandler onboardingHandler,
            DiscordSocketClient client)
        {
            _logger = logger;
            _onboardingHandler = onboardingHandler;
            _client = client;
        }

        private void SetupEventHandlers()
        {
            _client.Log += LogAsync;
            _client.Ready += ReadyAsync;
            _client.UserJoined += HandleUserJoinedAsync;
            _client.MessageReceived += HandleMessageReceivedAsync;
            _client.InteractionCreated += HandleInteractionCreatedAsync;
            _client.Disconnected += DisconnectedAsync;
        }

        public async Task StartAsync()
        {
            var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Discord token not found in environment variables");
                return;
            }

            SetupEventHandlers();
            
            // Add retry logic for rate limiting
            int maxRetries = 5;
            int retryCount = 0;
            
            while (retryCount < maxRetries)
            {
                try
                {
                    _logger.LogInformation("[Startup] Attempting to connect to Discord (attempt {Attempt}/{MaxAttempts})", retryCount + 1, maxRetries);
                    
                    await _client.LoginAsync(TokenType.Bot, token);
                    await _client.StartAsync();
                    
                    _logger.LogInformation("[Startup] Successfully connected to Discord");
                    break;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogWarning(ex, "[Startup] Failed to connect to Discord (attempt {Attempt}/{MaxAttempts})", retryCount, maxRetries);
                    
                    if (retryCount < maxRetries)
                    {
                        int delaySeconds = Math.Min(30 * retryCount, 120); // Exponential backoff with max 2 minutes
                        _logger.LogInformation("[Startup] Waiting {Delay} seconds before retry...", delaySeconds);
                        await Task.Delay(delaySeconds * 1000);
                    }
                    else
                    {
                        _logger.LogError(ex, "[Startup] Failed to connect to Discord after {MaxAttempts} attempts", maxRetries);
                        throw;
                    }
                }
            }

            // Register slash commands
            await RegisterCommandsAsync();
        }

        private async Task RegisterCommandsAsync()
        {
            try
            {
                // Register slash commands using SlashCommandBuilder
                var joinCommand = new Discord.SlashCommandBuilder()
                    .WithName("join")
                    .WithDescription("انضم إلى العائلة")
                    .Build();

                foreach (var guild in _client.Guilds)
                {
                    try
                    {
                        await guild.CreateApplicationCommandAsync(joinCommand);
                        _logger.LogInformation("[Commands] Registered /join command in guild: {GuildName}", guild.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Commands] Failed to register command in guild: {GuildName}", guild.Name);
                    }
                }
                
                _logger.LogInformation("Bot ready with slash commands");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Commands] Failed to register slash commands");
            }
        }

        private async Task DisconnectedAsync(Exception exception)
        {
            _logger.LogWarning(exception, "[Disconnected] Bot disconnected from Discord");
            
            // Wait a bit before attempting to reconnect
            await Task.Delay(5000);
            
            try
            {
                _logger.LogInformation("[Reconnection] Attempting to reconnect to Discord...");
                await _client.StartAsync();
                _logger.LogInformation("[Reconnection] Successfully reconnected to Discord");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Reconnection] Failed to reconnect to Discord");
            }
        }

        private Task LogAsync(LogMessage log)
        {
            _logger.LogInformation("[Discord.NET] {Message}", log.Message);
            return Task.CompletedTask;
        }

        private async Task ReadyAsync()
        {
            _logger.LogInformation("[Ready] {BotName} is connected!", _client.CurrentUser?.Username);

            // Initialize invite tracking
            foreach (var guild in _client.Guilds)
            {
                var invites = await guild.GetInvitesAsync();
                foreach (var invite in invites)
                {
                    _inviteUses[invite.Code] = invite.Uses ?? 0;
                }
            }
        }

        private async Task HandleUserJoinedAsync(SocketGuildUser user)
        {
            try
            {
                _logger.LogInformation("[UserJoined] {Username} joined.", user.Username);

                // Check if user has existing story in story channel
                var hasExistingStory = await CheckExistingStoryAsync(user);
                if (hasExistingStory)
                {
                    await HandleExistingUserAsync(user);
                    return;
                }

                // Handle new user onboarding
                await _onboardingHandler.HandleNewUserAsync(user, _inviteUses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Exception in HandleUserJoinedAsync");
            }
        }

        private async Task<bool> CheckExistingStoryAsync(SocketGuildUser user)
        {
            try
            {
                var storyChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");
                if (string.IsNullOrEmpty(storyChannelIdStr) || !ulong.TryParse(storyChannelIdStr, out var storyChannelId))
                    return false;

                var storyChannel = _client.GetChannel(storyChannelId) as IMessageChannel;
                
                if (storyChannel == null) return false;

                var messages = await storyChannel.GetMessagesAsync(100).FlattenAsync();
                return messages.Any(m => m.MentionedUserIds.Contains(user.Id));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to check existing story");
                return false;
            }
        }

        private async Task HandleExistingUserAsync(SocketGuildUser user)
        {
            try
            {
                // Remove Outsider role and add Associate role
                await UpdateUserRolesAsync(user, removeOutsider: true, addAssociate: true);

                // Send welcome back message
                var embed = new EmbedBuilder()
                    .WithTitle("🎭 مرحباً بعودتك!")
                    .WithDescription("انته قديم… كنت موجود قبل كده. تم تحديث وضعك.")
                    .WithColor(Color.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await user.SendMessageAsync(embed: embed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle existing user");
            }
        }

        private async Task HandleMessageReceivedAsync(SocketMessage message)
        {
            try
            {
                if (message.Author.IsBot) return;

                // Handle prefix commands (fallback)
                if (message.Content.StartsWith("/join"))
                {
                    var user = message.Author as SocketGuildUser;
                    if (user != null)
                    {
                        await HandleJoinCommandAsync(user, message.Channel);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle message");
            }
        }

        public async Task UpdateUserRolesAsync(SocketGuildUser user, bool removeOutsider = false, bool addAssociate = false)
        {
            try
            {
                var outsiderRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_OUTSIDER_ROLE_ID");
                var associateRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_ASSOCIATE_ROLE_ID");

                if (removeOutsider && !string.IsNullOrEmpty(outsiderRoleIdStr) && ulong.TryParse(outsiderRoleIdStr, out var outsiderRoleId))
                {
                    var outsiderRole = user.Guild.GetRole(outsiderRoleId);
                    if (outsiderRole != null && user.Roles.Contains(outsiderRole))
                    {
                        await user.RemoveRoleAsync(outsiderRole);
                    }
                }

                if (addAssociate && !string.IsNullOrEmpty(associateRoleIdStr) && ulong.TryParse(associateRoleIdStr, out var associateRoleId))
                {
                    var associateRole = user.Guild.GetRole(associateRoleId);
                    if (associateRole != null && !user.Roles.Contains(associateRole))
                    {
                        await user.AddRoleAsync(associateRole);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to update user roles");
            }
        }

        private async Task HandleJoinCommandAsync(SocketGuildUser user, ISocketMessageChannel channel)
        {
            try
            {
                // Check if user is in City Gates channel
                var cityGatesChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_CITY_GATES_CHANNEL_ID");
                if (string.IsNullOrEmpty(cityGatesChannelIdStr) || !ulong.TryParse(cityGatesChannelIdStr, out var cityGatesChannelId))
                {
                    await channel.SendMessageAsync("خطأ في إعدادات البوت.");
                    return;
                }
                
                if (channel.Id != cityGatesChannelId)
                {
                    await channel.SendMessageAsync("هذا الأمر متاح فقط في قناة City Gates.");
                    return;
                }

                // Check if user already has Associate role
                var associateRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_ASSOCIATE_ROLE_ID");
                if (!string.IsNullOrEmpty(associateRoleIdStr) && ulong.TryParse(associateRoleIdStr, out var associateRoleId))
                {
                    var associateRole = user.Guild.GetRole(associateRoleId);
                    if (associateRole != null && user.Roles.Contains(associateRole))
                    {
                        await channel.SendMessageAsync("أنت بالفعل عضو في العائلة!");
                        return;
                    }
                }

                // Check if user has existing story
                var hasExistingStory = await CheckExistingStoryAsync(user);
                if (hasExistingStory)
                {
                    await HandleExistingUserAsync(user, channel);
                    return;
                }

                // Start onboarding process
                await channel.SendMessageAsync("🎭 مرحباً بك! سيتم إنشاء thread خاص لبدء عملية الانضمام...");

                // Handle new user onboarding
                await HandleNewUserAsync(user, channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle join command");
                await channel.SendMessageAsync("حدث خطأ أثناء تنفيذ الأمر.");
            }
        }

        private async Task HandleExistingUserAsync(SocketGuildUser user, ISocketMessageChannel channel)
        {
            try
            {
                // Remove Outsider role and add Associate role
                await UpdateUserRolesAsync(user, removeOutsider: true, addAssociate: true);

                var embed = new EmbedBuilder()
                    .WithTitle("🎭 مرحباً بعودتك!")
                    .WithDescription("انته قديم… كنت موجود قبل كده. تم تحديث وضعك.")
                    .WithColor(Color.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await channel.SendMessageAsync(embed: embed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle existing user");
                await channel.SendMessageAsync("حدث خطأ أثناء تحديث وضعك.");
            }
        }

        private async Task HandleNewUserAsync(SocketGuildUser user, ISocketMessageChannel channel)
        {
            try
            {
                var inviteUses = _inviteUses;

                // Get inviter information
                var (inviterName, inviterId, inviterRole, inviterStory) = 
                    await _onboardingHandler.GetInviterInfoAsync(user, inviteUses);

                bool hasInvite = inviterId != 0 && inviterName != "غير معروف";

                // Conduct onboarding
                var userResponses = await _onboardingHandler.ConductOnboardingAsync(user);

                // Generate story
                _logger.LogInformation("[Join] Generating story for user {Username}", user.Username);
                var story = await _onboardingHandler.GenerateStoryAsync(userResponses, inviterName, inviterRole, inviterStory);

                // Save story
                _onboardingHandler.SaveStory(user.Id, story);

                // Send story to channel
                await _onboardingHandler.SendStoryToChannelAsync(user, story, hasInvite, this);

                // Update user roles
                await UpdateUserRolesAsync(user, removeOutsider: true, addAssociate: true);

                _logger.LogInformation("[Join] Completed onboarding for user {Username}", user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle new user in join command");
            }
        }

        private async Task HandleInteractionCreatedAsync(SocketInteraction interaction)
        {
            try
            {
                if (interaction is SocketSlashCommand slashCommand)
                {
                    if (slashCommand.Data.Name == "join")
                    {
                        var user = interaction.User as SocketGuildUser;
                        if (user != null)
                        {
                            await slashCommand.DeferAsync();
                            await HandleJoinCommandAsync(user, interaction.Channel);
                            await slashCommand.FollowupAsync("تم تنفيذ الأمر بنجاح!");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle interaction");
                try
                {
                    if (interaction.HasResponded)
                        await interaction.FollowupAsync("حدث خطأ أثناء تنفيذ الأمر.", ephemeral: true);
                    else
                        await interaction.RespondAsync("حدث خطأ أثناء تنفيذ الأمر.", ephemeral: true);
                }
                catch { }
            }
        }

        public DiscordSocketClient GetClient() => _client;
        public Dictionary<string, int> GetInviteUses() => _inviteUses;
    }
}
