using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Onboarding_bot.Services;

namespace Onboarding_bot.Handlers
{
    public class OnboardingHandler
    {
        private readonly ILogger<OnboardingHandler> _logger;
        private readonly OnboardingService _onboardingService;
        private readonly StoryService _storyService;

        public OnboardingHandler(
            ILogger<OnboardingHandler> logger,
            OnboardingService onboardingService,
            StoryService storyService)
        {
            _logger = logger;
            _onboardingService = onboardingService;
            _storyService = storyService;
        }

        public async Task HandleNewUserAsync(SocketGuildUser user, Dictionary<string, int> inviteUses)
        {
            try
            {
                _logger.LogInformation("[Onboarding] Starting onboarding for user {Username}", user.Username);

                // Get inviter information
                var (inviterName, inviterId, inviterRole, inviterStory) = 
                    await _onboardingService.GetInviterInfoAsync(user, inviteUses);

                bool hasInvite = inviterId != 0 && inviterName != "غير معروف";

                // Conduct onboarding
                var userResponses = await _onboardingService.ConductOnboardingAsync(user);

                // Generate story
                _logger.LogInformation("[Onboarding] Generating story for user {Username}", user.Username);
                var story = await _storyService.GenerateStoryAsync(userResponses, inviterName, inviterRole, inviterStory);

                // Save story
                _storyService.SaveStory(user.Id, story);

                // Send story to channel
                await _storyService.SendStoryToChannelAsync(user, story, hasInvite, null);

                // Update user roles - this will be handled by the calling service
                _logger.LogInformation("[Onboarding] User roles update requested for {Username}", user.Username);

                _logger.LogInformation("[Onboarding] Completed onboarding for user {Username}", user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to handle new user onboarding for {Username}", user.Username);
            }
        }

        public async Task<(string inviterName, ulong inviterId, string inviterRole, string inviterStory)> GetInviterInfoAsync(
            SocketGuildUser user, Dictionary<string, int> inviteUses)
        {
            return await _onboardingService.GetInviterInfoAsync(user, inviteUses);
        }

        public async Task<Dictionary<string, string>> ConductOnboardingAsync(SocketGuildUser user)
        {
            return await _onboardingService.ConductOnboardingAsync(user);
        }

        public async Task<string> GenerateStoryAsync(
            Dictionary<string, string> userResponses,
            string inviterName, string inviterRole, string inviterStory)
        {
            return await _storyService.GenerateStoryAsync(userResponses, inviterName, inviterRole, inviterStory);
        }

        public void SaveStory(ulong userId, string story)
        {
            _storyService.SaveStory(userId, story);
        }

        public async Task SendStoryToChannelAsync(SocketGuildUser user, string story, bool hasInvite, DiscordBotService discordService)
        {
            await _storyService.SendStoryToChannelAsync(user, story, hasInvite, discordService);
        }
    }
}
