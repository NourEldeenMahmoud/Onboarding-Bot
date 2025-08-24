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

                RestInviteMetadata usedInvite = null;
                foreach (var invite in invitesAfter)
                {
                    var previousUses = inviteUses.ContainsKey(invite.Code) ? inviteUses[invite.Code] : 0;
                    var currentUses = invite.Uses ?? 0;
                    
                    _logger.LogInformation("[Inviter] Invite {Code}: Previous uses: {Previous}, Current uses: {Current}", 
                        invite.Code, previousUses, currentUses);
                    
                    if (currentUses > previousUses)
                    {
                        usedInvite = invite;
                        _logger.LogInformation("[Inviter] Found used invite: {Code} by {InviterName}", 
                            invite.Code, invite.Inviter?.Username ?? "Unknown");
                        break;
                    }
                }

                // Update invite uses
                foreach (var invite in invitesAfter)
                {
                    inviteUses[invite.Code] = invite.Uses ?? 0;
                }

                string inviterName = usedInvite?.Inviter?.Username ?? "غير معروف";
                ulong inviterId = usedInvite?.Inviter?.Id ?? 0;
                string inviterStory = "";

                _logger.LogInformation("[Inviter] Final result for {Username}: Inviter={InviterName} ({InviterId})", 
                    user.Username, inviterName, inviterId);

                if (inviterId != 0)
                {
                    var inviterUser = guild.GetUser(inviterId);
                    var inviterRole = inviterUser?.Roles
                        .Where(r => r.Id != guild.EveryoneRole.Id)
                        .OrderByDescending(r => r.Position)
                        .FirstOrDefault()?.Name ?? "بدون رول";

                    inviterStory = _storyService.LoadStory(inviterId);

                    return (inviterName, inviterId, inviterRole, inviterStory);
                }

                return ("غير معروف", 0, "بدون رول", "");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to get inviter info");
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
                    .WithDescription($"أهلاً {user.Username}! 🎭\nقبل ما تبدأ، عايزين نعرف شوية حاجات عنك.")
                    .WithColor(Color.DarkBlue)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await thread.SendMessageAsync(embed: welcomeEmbed);

                // Ask questions
                responses["name"] = await AskQuestionAsync(thread, "اسمك الحقيقي ايه؟");
                responses["age"] = await AskQuestionAsync(thread, "سنك كام؟");
                responses["interest"] = await AskQuestionAsync(thread, "داخل السرفر ليه؟");
                responses["specialty"] = await AskQuestionAsync(thread, "تخصصك أو شغفك؟");
                responses["strength"] = await AskQuestionAsync(thread, "أهم ميزة عندك؟");
                responses["weakness"] = await AskQuestionAsync(thread, "أكبر عيب عندك؟");
                responses["favoritePlace"] = await AskQuestionAsync(thread, "مكان بتحبه تروح له؟");

                // Send completion message with story channel link
                var storyChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");
                var storyChannelLink = "";
                
                if (!string.IsNullOrEmpty(storyChannelIdStr) && ulong.TryParse(storyChannelIdStr, out var storyChannelId))
                {
                    storyChannelLink = $"<#{storyChannelId}>";
                }

                var completionEmbed = new EmbedBuilder()
                    .WithTitle("✅ تم الانتهاء!")
                    .WithDescription($"قصتك جاهزة… تقدر تشوفها في قناة القصص {storyChannelLink}")
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

        private async Task<string> AskQuestionAsync(IThreadChannel thread, string question)
        {
            var questionEmbed = new EmbedBuilder()
                .WithTitle("❓ سؤال")
                .WithDescription(question)
                .WithColor(Color.Blue)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await thread.SendMessageAsync(embed: questionEmbed);
            return await WaitForUserResponseAsync(thread);
        }

        private async Task<string> WaitForUserResponseAsync(IThreadChannel thread, int timeoutSeconds = 60)
        {
            var tcs = new TaskCompletionSource<string>();
            var userId = thread.OwnerId;
            var timeoutTask = Task.Delay(timeoutSeconds * 1000);

            // Create a local handler that only responds to messages in this specific thread
            Task Handler(SocketMessage msg)
            {
                if (msg.Channel.Id == thread.Id && msg.Author.Id == userId && !msg.Author.IsBot)
                {
                    _logger.LogInformation("[Response] User {Username} responded: {Response}", msg.Author.Username, msg.Content);
                    tcs.TrySetResult(msg.Content);
                }
                return Task.CompletedTask;
            }

            // Add the handler
            _client.MessageReceived += Handler;

            try
            {
                // Wait for either the user response or timeout
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                if (completedTask == tcs.Task)
                {
                    var response = await tcs.Task;
                    _logger.LogInformation("[Response] Successfully received response from user {Username}: {Response}", userId, response);
                    return response;
                }
                else
                {
                    _logger.LogWarning("[Timeout] User {UserId} did not respond in time.", userId);
                    return "لم يتم الرد في الوقت المحدد";
                }
            }
            finally
            {
                // Always remove the handler to prevent memory leaks
                _client.MessageReceived -= Handler;
            }
        }

        public async Task<bool> HasInviteAsync(SocketGuildUser user, Dictionary<string, int> inviteUses)
        {
            var (inviterName, inviterId, _, _) = await GetInviterInfoAsync(user, inviteUses);
            return inviterId != 0 && inviterName != "غير معروف";
        }
    }
}
