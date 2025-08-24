using Discord;
using Discord.WebSocket;
using Discord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Onboarding_bot.Services;
using System.Linq;

namespace Onboarding_bot.Services
{
    public class OnboardingService
    {
        private readonly ILogger<OnboardingService> _logger;
        private readonly StoryService _storyService;
        private readonly DiscordSocketClient _client;

        public OnboardingService(
            ILogger<OnboardingService> logger,
            StoryService storyService,
            DiscordSocketClient client)
        {
            _logger = logger;
            _storyService = storyService;
            _client = client;
        }

        public async Task<(string inviterName, ulong inviterId, string inviterRole, string inviterStory)> GetInviterInfoAsync(
            SocketGuildUser user, Dictionary<string, int> inviteUses)
        {
            try
            {
                var guild = user.Guild;
                var invitesAfter = await guild.GetInvitesAsync();

                _logger.LogInformation("[Inviter] Checking invites for user {Username}. Found {InviteCount} invites", user.Username, invitesAfter.Count);

                // Log all current invite states
                foreach (var invite in invitesAfter)
                {
                    var previousUses = inviteUses.ContainsKey(invite.Code) ? inviteUses[invite.Code] : 0;
                    var currentUses = invite.Uses ?? 0;
                    
                    _logger.LogInformation("[Inviter] Invite {Code}: Previous uses: {Previous}, Current uses: {Current}, Inviter: {InviterName}", 
                        invite.Code, previousUses, currentUses, invite.Inviter?.Username ?? "Unknown");
                }

                // Find the invite that was used by this user
                RestInviteMetadata? usedInvite = null;
                foreach (var invite in invitesAfter)
                {
                    var previousUses = inviteUses.ContainsKey(invite.Code) ? inviteUses[invite.Code] : 0;
                    var currentUses = invite.Uses ?? 0;
                    
                    // Check if this invite was used (current > previous)
                    if (currentUses > previousUses)
                    {
                        usedInvite = invite;
                        _logger.LogInformation("[Inviter] Found used invite: {Code} by {InviterName} (Uses increased from {Previous} to {Current})", 
                            invite.Code, invite.Inviter?.Username ?? "Unknown", previousUses, currentUses);
                        break;
                    }
                }

                // Update invite uses for future tracking
                foreach (var invite in invitesAfter)
                {
                    inviteUses[invite.Code] = invite.Uses ?? 0;
                }

                string inviterName = usedInvite?.Inviter?.Username ?? "غير معروف";
                ulong inviterId = usedInvite?.Inviter?.Id ?? 0;
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

                        inviterStory = _storyService.LoadStory(inviterId) ?? string.Empty;
                        
                        _logger.LogInformation("[Inviter] Inviter details: {InviterName} ({InviterId}), Role: {Role}, HasStory: {HasStory}", 
                            inviterName, inviterId, inviterRole, !string.IsNullOrEmpty(inviterStory));
                    }
                }

                _logger.LogInformation("[Inviter] Final result for {Username}: Inviter={InviterName} ({InviterId}), Role={Role}", 
                    user.Username, inviterName, inviterId, inviterRole);

                return (inviterName, inviterId, inviterRole, inviterStory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to get inviter info for user {Username}", user.Username);
                return ("غير معروف", 0, "بدون رول", "");
            }
        }

        public async Task<Dictionary<string, string>> ConductOnboardingAsync(SocketGuildUser user)
        {
            var responses = new Dictionary<string, string>();
            var thread = await CreatePrivateThreadAsync(user);

            try
            {
                // Send welcome message
                var welcomeEmbed = new EmbedBuilder()
                    .WithTitle("🎭 مرحباً بك في العائلة!")
                    .WithDescription($"أهلاً {user.Username}! 🎭\n  قبل ما تبدأ، عايزين نعرف شوية حاجات عنك. البوت هيسألك 4 اساله وانته هتجاوب عليهم , مش لازم تجاوب اجابات نموذجية او حقيقية 100%  ")
                    .WithColor(Color.DarkBlue)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await thread.SendMessageAsync(embed: welcomeEmbed);

                // Ask questions
                responses["expectation"] = await AskQuestionAsync(thread, user, "متوقع انك تستفيد إيه من السيرفر ده؟");
                responses["mafiaNickname"] = await AskQuestionAsync(thread, user, "لو إنت عضو في المافيا الإيطالية، لقبك هيبقى إيه؟");
                responses["superpower"] = await AskQuestionAsync(thread, user, "لو معاك قدرة خارقة واحدة بس، تختار تبقى إيه وليه؟");
                responses["prosAndCons"] = await AskQuestionAsync(thread, user, "ايه اهم ميزه , اكبر عيب فيك ؟");

                // Send completion message with story channel link
                var storyChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");
                var storyChannelLink = "";
                
                if (!string.IsNullOrEmpty(storyChannelIdStr) && ulong.TryParse(storyChannelIdStr, out var storyChannelId))
                {
                    storyChannelLink = $"<#{storyChannelId}>";
                }

                var completionEmbed = new EmbedBuilder()
                    .WithTitle("✅ تم الانتهاء!")
                    .WithDescription($"قصتك هتبقي جاهزه في خلال دقيقة … تقدر تشوفها في قناة القصص {storyChannelLink}")
                    .WithColor(Color.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await thread.SendMessageAsync(embed: completionEmbed);

                return responses;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to conduct onboarding");
                throw;
            }
            finally
            {
                // Don't delete the thread automatically - let the user leave it manually
                // This prevents the "gateway task blocking" issue
                _logger.LogInformation("[Onboarding] Onboarding completed for user {Username}. Thread will remain open.", user.Username);
            }
        }

        private async Task<IThreadChannel> CreatePrivateThreadAsync(SocketGuildUser user)
        {
            try
            {
                var cityGatesChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_CITY_GATES_CHANNEL_ID");
                if (string.IsNullOrEmpty(cityGatesChannelIdStr) || !ulong.TryParse(cityGatesChannelIdStr, out var cityGatesChannelId))
                {
                    throw new InvalidOperationException("City Gates channel ID not found in environment variables");
                }

                var cityGatesChannel = user.Guild.GetChannel(cityGatesChannelId) as ITextChannel;

                if (cityGatesChannel == null)
                {
                    throw new InvalidOperationException("City Gates channel not found");
                }

                var thread = await cityGatesChannel.CreateThreadAsync(
                    name: $"onboarding-{user.Username}",
                    type: ThreadType.PrivateThread,
                    invitable: false);

                await thread.AddUserAsync(user);
                return thread;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to create private thread");
                throw;
            }
        }

        private async Task<string> AskQuestionAsync(IThreadChannel thread, SocketGuildUser user, string question)
        {
            var questionEmbed = new EmbedBuilder()
                .WithTitle("❓ سؤال")
                .WithDescription(question)
                .WithColor(Color.Blue)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await thread.SendMessageAsync(embed: questionEmbed);
            return await WaitForUserResponseAsync(thread, user);
        }

        private async Task<string> WaitForUserResponseAsync(IThreadChannel thread, SocketGuildUser user, int timeoutSeconds = 180)
        {
            var userId = user.Id; // Use the actual user ID, not thread owner
            var startTime = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            
            _logger.LogInformation("[Response] Waiting for response from user {Username} ({UserId}) in thread {ThreadId}", user.Username, userId, thread.Id);

            // Get the timestamp when we started waiting (after sending the question)
            var questionSentTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < timeout)
            {
                try
                {
                    // Get recent messages in the thread
                    var messages = await thread.GetMessagesAsync(10).FlattenAsync();
                    
                    // Look for the most recent message from the user that came AFTER we sent the question
                    var userMessage = messages
                        .Where(m => m.Author.Id == userId && !m.Author.IsBot && m.Timestamp > questionSentTime)
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();

                    if (userMessage != null)
                    {
                        _logger.LogInformation("[Response] Found response from user {Username} after question: {Response}", 
                            userMessage.Author.Username, userMessage.Content);
                        return userMessage.Content;
                    }

                    // Wait a bit before checking again
                    await Task.Delay(1000); // Check every second
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Response] Error while waiting for response");
                    await Task.Delay(1000);
                }
            }

            _logger.LogWarning("[Response] Timeout waiting for response from user {Username} ({UserId})", user.Username, userId);
            return "لم يتم الرد في الوقت المحدد";
        }

        public async Task<bool> HasInviteAsync(SocketGuildUser user, Dictionary<string, int> inviteUses)
        {
            var (inviterName, inviterId, _, _) = await GetInviterInfoAsync(user, inviteUses);
            return inviterId != 0 && inviterName != "غير معروف";
        }
    }
}
