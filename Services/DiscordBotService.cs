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
                _logger.LogInformation("[Commands] Starting command registration...");
                
                // Register slash commands using SlashCommandBuilder
                var joinCommand = new Discord.SlashCommandBuilder()
                    .WithName("join")
                    .WithDescription("انضم إلى العائلة")
                    .Build();

                _logger.LogInformation("[Commands] Built join command: {CommandName}", joinCommand.Name);

                foreach (var guild in _client.Guilds)
                {
                    try
                    {
                        _logger.LogInformation("[Commands] Registering command in guild: {GuildName} ({GuildId})", guild.Name, guild.Id);
                        await guild.CreateApplicationCommandAsync(joinCommand);
                        _logger.LogInformation("[Commands] Successfully registered /join command in guild: {GuildName}", guild.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Commands] Failed to register command in guild: {GuildName}", guild.Name);
                    }
                }
                
                _logger.LogInformation("[Commands] Command registration completed");
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
                try
                {
                    var invites = await guild.GetInvitesAsync();
                    _logger.LogInformation("[Ready] Found {InviteCount} invites in guild {GuildName}", invites.Count, guild.Name);
                    
                    foreach (var invite in invites)
                    {
                        _inviteUses[invite.Code] = invite.Uses ?? 0;
                        _logger.LogInformation("[Ready] Tracked invite {Code} with {Uses} uses", invite.Code, invite.Uses ?? 0);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Ready] Failed to get invites for guild {GuildName}", guild.Name);
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

                // For new users, just add Outsider role - DON'T start onboarding automatically
                // Onboarding only starts when user types /join command
                await UpdateUserRolesAsync(user, removeOutsider: false, addOutsider: true);
                _logger.LogInformation("[UserJoined] Added Outsider role to new user {Username}. Waiting for /join command.", user.Username);
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
                {
                    _logger.LogWarning("[CheckStory] Story channel ID not found in environment variables");
                    return false;
                }

                var storyChannel = _client.GetChannel(storyChannelId) as IMessageChannel;
                
                if (storyChannel == null)
                {
                    _logger.LogWarning("[CheckStory] Story channel not found: {ChannelId}", storyChannelId);
                    return false;
                }

                // Get recent messages (last 200 messages to be sure)
                var messages = await storyChannel.GetMessagesAsync(200).FlattenAsync();
                _logger.LogInformation("[CheckStory] Checking {MessageCount} messages in story channel for user {Username}", messages.Count(), user.Username);
                
                foreach (var message in messages)
                {
                    // Check if user is mentioned in the message
                    if (message.MentionedUserIds.Contains(user.Id))
                    {
                        _logger.LogInformation("[CheckStory] Found existing story for user {Username} by mention in message {MessageId} (Content: {Content})", 
                            user.Username, message.Id, message.Content?.Substring(0, Math.Min(50, message.Content?.Length ?? 0)));
                        return true;
                    }
                    
                    // Check if the message content contains the user's name or username
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        var content = message.Content.ToLowerInvariant();
                        var username = user.Username.ToLowerInvariant();
                        var nickname = (user.Nickname ?? "").ToLowerInvariant();
                        
                        if (content.Contains(username) || (!string.IsNullOrEmpty(nickname) && content.Contains(nickname)))
                        {
                            _logger.LogInformation("[CheckStory] Found existing story for user {Username} by name match in message {MessageId} (Content: {Content})", 
                                user.Username, message.Id, message.Content.Substring(0, Math.Min(50, message.Content.Length)));
                            return true;
                        }
                    }
                }
                
                _logger.LogInformation("[CheckStory] No existing story found for user {Username} in story channel. User is considered NEW.", user.Username);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to check existing story for user {Username}", user.Username);
                return false;
            }
        }

        private async Task HandleExistingUserAsync(SocketGuildUser user, ISocketMessageChannel? channel = null)
        {
            try
            {
                // Remove Outsider role and add Associate role
                await UpdateUserRolesAsync(user, removeOutsider: true, addAssociate: true);

                // Send message in City Gates channel if channel is provided
                if (channel != null)
                {
                    var embed = new EmbedBuilder()
                    .WithTitle("🎭 مرحباً بعودتك!")
                    .WithDescription("انته عضو قديم في العائلة… welcome back\n Role : Associate .")
                    .WithColor(Color.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                    await channel.SendMessageAsync(embed: embed);
                }

                // Always send welcome back message to STORY CHANNEL
                var storyChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");
                if (!string.IsNullOrEmpty(storyChannelIdStr) && ulong.TryParse(storyChannelIdStr, out var storyChannelId))
                {
                    var storyChannel = _client.GetChannel(storyChannelId) as IMessageChannel;
                    if (storyChannel != null)
                    {
                        var storyEmbed = new EmbedBuilder()
                            .WithTitle("🎭 عضو قديم عاد!")
                            .WithDescription($"**{user.Username}** عضو قديم ورجع للعائلة! 🎉")
                            .WithColor(Color.Green)
                            .WithThumbnailUrl(user.GetAvatarUrl() ?? "")
                            .WithTimestamp(DateTimeOffset.UtcNow)
                            .WithFooter(footer => footer.Text = "🟢 عضو قديم")
                            .Build();

                        await storyChannel.SendMessageAsync(embed: storyEmbed);
                        _logger.LogInformation("[ExistingUser] Sent welcome back message to story channel for {Username}", user.Username);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle existing user");
                if (channel != null)
                {
                    try
                    {
                        await channel.SendMessageAsync("حدث خطأ أثناء تحديث وضعك.");
                    }
                    catch { }
                }
            }
        }

        private async Task HandleMessageReceivedAsync(SocketMessage message)
        {
            try
            {
                if (message.Author.IsBot) return;

                // Only handle prefix commands if slash commands fail
                if (message.Content.StartsWith("/join"))
                {
                    var user = message.Author as SocketGuildUser;
                    if (user != null)
                    {
                        _logger.LogInformation("[Message] Handling /join command from {Username}", user.Username);
                        await HandleJoinCommandAsync(user, message.Channel);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle message");
            }
        }

        public async Task UpdateUserRolesAsync(SocketGuildUser user, bool removeOutsider = false, bool addAssociate = false, bool addOutsider = false)
        {
            try
            {
                var outsiderRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_OUTSIDER_ROLE_ID");
                var associateRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_ASSOCIATE_ROLE_ID");

                if (addOutsider && !string.IsNullOrEmpty(outsiderRoleIdStr) && ulong.TryParse(outsiderRoleIdStr, out var outsiderRoleId))
                {
                    var outsiderRole = user.Guild.GetRole(outsiderRoleId);
                    if (outsiderRole != null && !user.Roles.Contains(outsiderRole))
                    {
                        await user.AddRoleAsync(outsiderRole);
                        _logger.LogInformation("[Roles] Added Outsider role to user {Username}", user.Username);
                    }
                }

                if (removeOutsider && !string.IsNullOrEmpty(outsiderRoleIdStr) && ulong.TryParse(outsiderRoleIdStr, out var outsiderRoleIdToRemove))
                {
                    var outsiderRole = user.Guild.GetRole(outsiderRoleIdToRemove);
                    if (outsiderRole != null && user.Roles.Contains(outsiderRole))
                    {
                        await user.RemoveRoleAsync(outsiderRole);
                        _logger.LogInformation("[Roles] Removed Outsider role from user {Username}", user.Username);
                    }
                }

                if (addAssociate && !string.IsNullOrEmpty(associateRoleIdStr) && ulong.TryParse(associateRoleIdStr, out var associateRoleId))
                {
                    var associateRole = user.Guild.GetRole(associateRoleId);
                    if (associateRole != null && !user.Roles.Contains(associateRole))
                    {
                        await user.AddRoleAsync(associateRole);
                        _logger.LogInformation("[Roles] Added Associate role to user {Username}", user.Username);
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
                var DISCORD_STORY_CHANNEL_ID = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");

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

        private async Task HandleNewUserAsync(SocketGuildUser user, ISocketMessageChannel channel)
        {
            try
            {
                _logger.LogInformation("[NewUser] Starting onboarding for user {Username}", user.Username);

                // Get current invite uses to compare
                var currentInvites = await user.Guild.GetInvitesAsync();
                var inviteUses = _inviteUses;

                // Get inviter information
                var (inviterName, inviterId, inviterRole, inviterStory) = 
                    await _onboardingHandler.GetInviterInfoAsync(user, inviteUses);

                bool hasInvite = inviterId != 0 && inviterName != "غير معروف";
                
                _logger.LogInformation("[NewUser] User {Username} - HasInvite: {HasInvite}, Inviter: {InviterName} ({InviterId})", 
                    user.Username, hasInvite, inviterName, inviterId);

                // Conduct onboarding
                var userResponses = await _onboardingHandler.ConductOnboardingAsync(user);

                // Generate story
                _logger.LogInformation("[Join] Generating story for user {Username}", user.Username);
                var story = await _onboardingHandler.GenerateStoryAsync(userResponses, inviterName, inviterRole, inviterStory);

                // Save story
                _onboardingHandler.SaveStory(user.Id, story);

                // Send story to channel - pass this DiscordBotService instance
                await _onboardingHandler.SendStoryToChannelAsync(user, story, hasInvite, this);

                // Update user roles
                await UpdateUserRolesAsync(user, removeOutsider: true, addAssociate: true);

                // Update invite tracking
                foreach (var invite in currentInvites)
                {
                    _inviteUses[invite.Code] = invite.Uses ?? 0;
                }

                _logger.LogInformation("[Join] Completed onboarding for user {Username} with invite status: {HasInvite}", user.Username, hasInvite);
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
                    _logger.LogInformation("[Interaction] Received slash command: {CommandName}", slashCommand.Data.Name);
                    
                    if (slashCommand.Data.Name == "join")
                    {
                        var user = interaction.User as SocketGuildUser;
                        if (user != null)
                        {
                            _logger.LogInformation("[Interaction] Processing /join for user: {Username}", user.Username);
                            
                            try
                            {
                                await slashCommand.DeferAsync();
                                await HandleJoinCommandAsync(user, interaction.Channel);
                                await slashCommand.FollowupAsync("تم تنفيذ الأمر بنجاح!");
                                _logger.LogInformation("[Interaction] Successfully processed /join for {Username}", user.Username);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[Interaction] Error processing /join for {Username}", user.Username);
                                await slashCommand.FollowupAsync("حدث خطأ أثناء تنفيذ الأمر.", ephemeral: true);
                            }
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
