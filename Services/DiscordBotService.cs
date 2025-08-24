using Discord;
using Discord.WebSocket;
using Discord.Rest;
using Microsoft.Extensions.Logging;
using Onboarding_bot.Handlers;

namespace Onboarding_bot.Services
{
    public class DiscordBotService
    {
        private readonly DiscordSocketClient _client;
        private readonly ILogger<DiscordBotService> _logger;
        private readonly OnboardingHandler _onboardingHandler;
        // IMPROVED: Use cache per guild instead of single dictionary
        private readonly Dictionary<ulong, List<RestInviteMetadata>> _inviteCache = new();

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
            _client.GuildAvailable += GuildAvailableAsync; // Add this to track invites when guild becomes available
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

        private Task DisconnectedAsync(Exception exception)
        {
            _logger.LogWarning(exception, "[Disconnected] Bot disconnected from Discord");
            
            // Don't block the gateway task - use Task.Run for reconnection
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait a bit before attempting to reconnect
                    await Task.Delay(5000);
                    
                    _logger.LogInformation("[Reconnection] Attempting to reconnect to Discord...");
                    
                    // Try to reconnect multiple times
                    int maxReconnectAttempts = 3;
                    for (int attempt = 1; attempt <= maxReconnectAttempts; attempt++)
                    {
                        try
                        {
                            if (_client.ConnectionState == ConnectionState.Disconnected)
                            {
                                await _client.StartAsync();
                                _logger.LogInformation("[Reconnection] Successfully reconnected to Discord on attempt {Attempt}", attempt);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Reconnection] Failed to reconnect on attempt {Attempt}/{MaxAttempts}", attempt, maxReconnectAttempts);
                            
                            if (attempt < maxReconnectAttempts)
                            {
                                int delaySeconds = Math.Min(10 * attempt, 30); // Exponential backoff with max 30 seconds
                                await Task.Delay(delaySeconds * 1000);
                            }
                        }
                    }
                    
                    _logger.LogError("[Reconnection] Failed to reconnect after {MaxAttempts} attempts", maxReconnectAttempts);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Reconnection] Unexpected error during reconnection");
                }
            });
            
            return Task.CompletedTask;
        }

        private Task LogAsync(LogMessage log)
        {
            _logger.LogInformation("[Discord.NET] {Message}", log.Message);
            return Task.CompletedTask;
        }

        private Task ReadyAsync()
        {
            _logger.LogInformation("[Ready] {BotName} is connected!", _client.CurrentUser?.Username);
            
            // GuildAvailable event will handle invite tracking for each guild
            // No need to do it here to avoid duplicate work
            
            return Task.CompletedTask;
        }

        private async Task GuildAvailableAsync(SocketGuild guild)
        {
            _logger.LogInformation("[GuildAvailable] Guild {GuildName} ({GuildId}) is now available. Tracking invites...", guild.Name, guild.Id);
            
            try
            {
                var invites = await guild.GetInvitesAsync();
                _logger.LogInformation("[GuildAvailable] Found {InviteCount} invites in guild {GuildName}", invites.Count, guild.Name);
                
                // Cache the invites for this guild
                _inviteCache[guild.Id] = invites.ToList();
                _logger.LogInformation("[GuildAvailable] Invite cache initialized for guild {GuildName}", guild.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GuildAvailable] Failed to get invites for guild {GuildName}", guild.Name);
            }
        }

        private async Task HandleUserJoinedAsync(SocketGuildUser user)
        {
            try
            {
                _logger.LogInformation("[UserJoined] {Username} joined.", user.Username);

                // Track invite usage immediately when user joins
                await TrackInviteUsageAsync(user);
                
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
                _logger.LogError(ex, "[Error] Exception in HandleUserJoinedAsync for {Username}", user.Username);
            }
        }

        // NEW: Improved invite tracking method
        private async Task TrackInviteUsageAsync(SocketGuildUser user)
        {
            try
            {
                var guild = user.Guild;
                
                // Check if we have cached invites for this guild
                if (!_inviteCache.ContainsKey(guild.Id))
                {
                    _logger.LogWarning("[InviteTracking] No cached invites found for guild {GuildName}. Initializing...", guild.Name);
                    var invites = await guild.GetInvitesAsync();
                    _inviteCache[guild.Id] = invites.ToList();
                    return;
                }

                var oldInvites = _inviteCache[guild.Id];
                var newInvites = await guild.GetInvitesAsync();

                // Find the invite that was used by this user
                var usedInvite = newInvites.FirstOrDefault(inv => 
                {
                    var oldInvite = oldInvites.FirstOrDefault(old => old.Code == inv.Code);
                    if (oldInvite != null)
                    {
                        var oldUses = oldInvite.Uses ?? 0;
                        var newUses = inv.Uses ?? 0;
                        return newUses > oldUses;
                    }
                    return false;
                });

                if (usedInvite != null)
                {
                    var inviterName = usedInvite.Inviter?.Username ?? "Unknown";
                    var oldInvite = oldInvites.First(old => old.Code == usedInvite.Code);
                    var oldUses = oldInvite.Uses ?? 0;
                    var newUses = usedInvite.Uses ?? 0;
                    
                    _logger.LogInformation("[InviteTracking] {Username} joined via invite {Code} from {InviterName} (Uses: {OldUses} → {NewUses})", 
                        user.Username, usedInvite.Code, inviterName, oldUses, newUses);
                }
                else
                {
                    _logger.LogInformation("[InviteTracking] {Username} joined without a known invite", user.Username);
                }

                // Update the cache with new invite data
                _inviteCache[guild.Id] = newInvites.ToList();
                _logger.LogInformation("[InviteTracking] Updated invite cache for guild {GuildName}", guild.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to track invite usage for user {Username}", user.Username);
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

                // IMPROVED: Check more messages and use better search strategy
                var messages = await storyChannel.GetMessagesAsync(200).FlattenAsync();
                _logger.LogInformation("[CheckStory] Checking {MessageCount} messages in story channel for user {Username}", messages.Count(), user.Username);
                
                // IMPROVED: Better search strategy
                var hasExistingStory = messages.Any(message => 
                {
                    // Check if user is mentioned in the message (most reliable)
                    if (message.MentionedUserIds.Contains(user.Id))
                    {
                        _logger.LogInformation("[CheckStory] Found existing story for user {Username} by mention in message {MessageId}", 
                            user.Username, message.Id);
                        return true;
                    }
                    
                    // Check if the message is an embed and contains user info
                    if (message.Embeds.Count > 0)
                    {
                        foreach (var embed in message.Embeds)
                        {
                            // Check embed title
                            if (!string.IsNullOrEmpty(embed.Title) && 
                                (embed.Title.Contains(user.Username) || 
                                 (user.Nickname != null && embed.Title.Contains(user.Nickname))))
                            {
                                _logger.LogInformation("[CheckStory] Found existing story for user {Username} by embed title in message {MessageId}", 
                                    user.Username, message.Id);
                                return true;
                            }
                            
                            // Check embed description
                            if (!string.IsNullOrEmpty(embed.Description) && 
                                (embed.Description.Contains(user.Username) || 
                                 (user.Nickname != null && embed.Description.Contains(user.Nickname))))
                            {
                                _logger.LogInformation("[CheckStory] Found existing story for user {Username} by embed description in message {MessageId}", 
                                    user.Username, message.Id);
                                return true;
                            }
                            
                            // Check embed footer
                            if (embed.Footer.HasValue && !string.IsNullOrEmpty(embed.Footer.Value.Text) && 
                                (embed.Footer.Value.Text.Contains(user.Username) || 
                                 (user.Nickname != null && embed.Footer.Value.Text.Contains(user.Nickname))))
                            {
                                _logger.LogInformation("[CheckStory] Found existing story for user {Username} by embed footer in message {MessageId}", 
                                    user.Username, message.Id);
                                return true;
                            }
                        }
                    }
                    
                    // Check if the message content contains the user's name or username
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        var content = message.Content.ToLowerInvariant();
                        var username = user.Username.ToLowerInvariant();
                        var nickname = (user.Nickname ?? "").ToLowerInvariant();
                        
                        if (content.Contains(username) || (!string.IsNullOrEmpty(nickname) && content.Contains(nickname)))
                        {
                            _logger.LogInformation("[CheckStory] Found existing story for user {Username} by name match in message {MessageId}", 
                                user.Username, message.Id);
                            return true;
                        }
                    }
                    
                    return false;
                });
                
                if (hasExistingStory)
                {
                    _logger.LogInformation("[CheckStory] User {Username} has existing story - considered OLD member", user.Username);
                    return true;
                }
                
                // FALLBACK: Check if user has a story saved in JSON file
                try
                {
                    var storyService = _onboardingHandler.GetStoryService();
                    var savedStory = storyService.LoadStory(user.Id);
                    if (!string.IsNullOrEmpty(savedStory))
                    {
                        _logger.LogInformation("[CheckStory] Found existing story for user {Username} in JSON file - considered OLD member", user.Username);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CheckStory] Failed to check JSON file for user {Username}", user.Username);
                }
                
                _logger.LogInformation("[CheckStory] No existing story found for user {Username} in story channel or JSON file. User is considered NEW.", user.Username);
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

        private Task HandleMessageReceivedAsync(SocketMessage message)
        {
            try
            {
                if (message.Author.IsBot) return Task.CompletedTask;

                // Only handle prefix commands if slash commands fail
                if (message.Content.StartsWith("/join"))
                {
                    var user = message.Author as SocketGuildUser;
                    if (user != null)
                    {
                        _logger.LogInformation("[Message] Handling /join command from {Username}", user.Username);
                        
                        // Handle join command in background to prevent blocking
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await HandleJoinCommandAsync(user, message.Channel);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[Error] Failed to handle join command for {Username}", user.Username);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle message");
            }
            
            return Task.CompletedTask;
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
                        
                        // AUTOMATIC: Remove Outsider role when Associate role is added
                        if (!string.IsNullOrEmpty(outsiderRoleIdStr) && ulong.TryParse(outsiderRoleIdStr, out var autoOutsiderRoleId))
                        {
                            var outsiderRole = user.Guild.GetRole(autoOutsiderRoleId);
                            if (outsiderRole != null && user.Roles.Contains(outsiderRole))
                            {
                                await user.RemoveRoleAsync(outsiderRole);
                                _logger.LogInformation("[Roles] Automatically removed Outsider role from user {Username} after adding Associate role", user.Username);
                            }
                        }
                    }
                }
                
                // FINAL CHECK: Ensure no user has both Outsider and Associate roles
                // This is a safety measure to maintain role consistency
                if (!string.IsNullOrEmpty(outsiderRoleIdStr) && !string.IsNullOrEmpty(associateRoleIdStr) && 
                    ulong.TryParse(outsiderRoleIdStr, out var finalOutsiderRoleId) && 
                    ulong.TryParse(associateRoleIdStr, out var finalAssociateRoleId))
                {
                    var outsiderRole = user.Guild.GetRole(finalOutsiderRoleId);
                    var associateRole = user.Guild.GetRole(finalAssociateRoleId);
                    
                    if (outsiderRole != null && associateRole != null && 
                        user.Roles.Contains(associateRole) && user.Roles.Contains(outsiderRole))
                    {
                        await user.RemoveRoleAsync(outsiderRole);
                        _logger.LogInformation("[Roles] Safety check: Removed conflicting Outsider role from user {Username} who has Associate role", user.Username);
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

                // Get inviter information using the cached invite data
                var (inviterName, inviterId, inviterRole, inviterStory) = 
                    await GetInviterInfoFromCacheAsync(user);

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

                _logger.LogInformation("[Join] Completed onboarding for user {Username} with invite status: {HasInvite}", user.Username, hasInvite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle new user in join command");
            }
        }

        // NEW: Get inviter info from cache instead of old dictionary
        public async Task<(string inviterName, ulong inviterId, string inviterRole, string inviterStory)> GetInviterInfoFromCacheAsync(SocketGuildUser user)
        {
            try
            {
                var guild = user.Guild;
                
                if (!_inviteCache.ContainsKey(guild.Id))
                {
                    _logger.LogWarning("[InviterInfo] No cached invites found for guild {GuildName}", guild.Name);
                    return ("غير معروف", 0, "بدون رول", "");
                }

                var oldInvites = _inviteCache[guild.Id];
                var newInvites = await guild.GetInvitesAsync();

                // Find the invite that was used by this user
                var usedInvite = newInvites.FirstOrDefault(inv => 
                {
                    var oldInvite = oldInvites.FirstOrDefault(old => old.Code == inv.Code);
                    if (oldInvite != null)
                    {
                        var oldUses = oldInvite.Uses ?? 0;
                        var newUses = inv.Uses ?? 0;
                        return newUses > oldUses;
                    }
                    return false;
                });

                if (usedInvite == null)
                {
                    _logger.LogInformation("[InviterInfo] No invite found for user {Username}", user.Username);
                    return ("غير معروف", 0, "بدون رول", "");
                }

                var inviterName = usedInvite.Inviter?.Username ?? "غير معروف";
                var inviterId = usedInvite.Inviter?.Id ?? 0;
                string inviterRole = string.Empty;
                string inviterStory = string.Empty;

                if (inviterId != 0)
                {
                    var inviterUser = guild.GetUser(inviterId);
                    if (inviterUser != null)
                    {
                        var topRole = inviterUser.Roles
                            .Where(r => r.Id != guild.EveryoneRole.Id)
                            .OrderByDescending(r => r.Position)
                            .FirstOrDefault();
                        inviterRole = topRole?.Name ?? "بدون رول";

                        inviterStory = _onboardingHandler.GetStoryService().LoadStory(inviterId) ?? string.Empty;
                        
                        _logger.LogInformation("[InviterInfo] Inviter details: {InviterName} ({InviterId}), Role: {Role}, HasStory: {HasStory}", 
                            inviterName, inviterId, inviterRole, !string.IsNullOrEmpty(inviterStory));
                    }
                }

                _logger.LogInformation("[InviterInfo] Final result for {Username}: Inviter={InviterName} ({InviterId}), Role={Role}, UsedInvite={UsedInviteCode}", 
                    user.Username, inviterName, inviterId, inviterRole, usedInvite.Code);

                return (inviterName, inviterId, inviterRole, inviterStory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to get inviter info from cache for user {Username}", user.Username);
                return ("غير معروف", 0, "بدون رول", "");
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

        // HELPER: Ensure role consistency - Associate users should never have Outsider role
        public async Task EnsureRoleConsistencyAsync(SocketGuildUser user)
        {
            try
            {
                var outsiderRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_OUTSIDER_ROLE_ID");
                var associateRoleIdStr = Environment.GetEnvironmentVariable("DISCORD_ASSOCIATE_ROLE_ID");
                
                if (string.IsNullOrEmpty(outsiderRoleIdStr) || string.IsNullOrEmpty(associateRoleIdStr) ||
                    !ulong.TryParse(outsiderRoleIdStr, out var outsiderRoleId) || 
                    !ulong.TryParse(associateRoleIdStr, out var associateRoleId))
                {
                    return;
                }
                
                var outsiderRole = user.Guild.GetRole(outsiderRoleId);
                var associateRole = user.Guild.GetRole(associateRoleId);
                
                if (outsiderRole != null && associateRole != null && 
                    user.Roles.Contains(associateRole) && user.Roles.Contains(outsiderRole))
                {
                    await user.RemoveRoleAsync(outsiderRole);
                    _logger.LogInformation("[RoleConsistency] Removed conflicting Outsider role from user {Username} who has Associate role", user.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to ensure role consistency for user {Username}", user.Username);
            }
        }

        public DiscordSocketClient GetClient() => _client;
        public Dictionary<ulong, List<RestInviteMetadata>> GetInviteCache() => _inviteCache;
    }
}
