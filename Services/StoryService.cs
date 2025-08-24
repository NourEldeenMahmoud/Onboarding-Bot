using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System.IO;

namespace Onboarding_bot.Services
{
    public class StoryService
    {
        private readonly ILogger<StoryService> _logger;
        private const string StoriesFile = "stories.json";

        public StoryService(
            ILogger<StoryService> logger)
        {
            _logger = logger;
        }

        public async Task<string> GenerateStoryAsync(
            Dictionary<string, string> userResponses,
            string inviterName, string inviterRole, string inviterStory)
        {
                       var prompt = $@"
أنت كاتب قصص Roleplay لعالم المافيا في سيرفر Discord اسمه ""the DevMob"".  
المدينة اسمها ""The Underworld""، مليانة أماكن زي: Coding Alley، Debuggers Street، 
The Underworld Casino، Don's Office، The Underworld Academy، Police HQ، Black Market، 
Hidden Docks، Tech Lab، Abandoned Warehouse، وغيرها.

السيرفر قايم على نظام طبقي من الرولز:
- The Don: رأس العصابة، أعلى سلطة.
- Consigliere: المستشار المقرب للـ Don.
- The Don's Kin: أفراد العائلة المقربة للـ Don.
- Associate: أعضاء معتمدين في العصابة.
- Outsider: أشخاص برّه العصابة بس ليهم تواصل محدود.
- Shady Snitch: العملاء أو المخبرين اللي عندهم معلومات سرية (ده رول مش اسم شخص).

🎯 مهمتك: ابتكر قصة قصيرة وحماسية للعضو الجديد بحيث تبقى **ديناميكية، مفاجئة، مافياوية** 
وفيها **لمسة هزلية ساخرة خفيفة**. 
العضو الجديد يبقى محور الأحداث، وكمان الشخص اللي دعاه يظهر في القصة بشكل مباشر.  

---

### المطلوب في القصة:

1. **اسم مستعار مافياوي** للعضو الجديد، مستوحى من اسمه أو مهارته.  
2. **خلفية درامية**: انضمامه مش بسهولة → لازم يحصل موقف مثير، مطاردة، تحدي، مهمة خطرة، أو صدفة غير متوقعة.  
3. **دمج الشخص اللي دعاه**: استخدم اسمه المستعار، الرول بتاعه، وجزء من قصته السابقة في أحداث القصة.  
4. **الأحداث تبقى مشوقة**: فيها مصطلحات المافيا (عمليات سرية، تهديدات، ملاحقات، صفقات سوداء، اختراقات).  
5. **لمسات برمجية**: المهارات التقنية أو الاختراقية ممكن تنقذ الموقف أو تكشف سر، 
بس خلي الجو العام مافيوي.  
6. **طابع هزلي/ساخر**: مش جد أوي، في نكهة كوميدية خفيفة.  
7. القصة تبقى **قصيرة، ممتعة، مليانة مفاجآت**.

---

### بيانات العضو الجديد:
- التوقعات من السيرفر: {userResponses["expectation"]}
- اللقب المافياوي: {userResponses["mafiaNickname"]}
- القدرة الخارقة: {userResponses["superpower"]}
- الميزة والعيب: {userResponses["prosAndCons"]}

### بيانات الشخص اللي دعاه:
- الاسم المستعار: {inviterName}
- الرول: {inviterRole}
- قصته السابقة: {inviterStory}

---

⚠ تعليمات نهائية مهمة:
- لا تكرر نفس القصة أبداً.  
- استخدم **كل معلومة** من العضو الجديد.  
- اجعل القصة **فريدة ومختلفة** كل مرة.  
- خلي الأسلوب **إبداعي ومشوق**.  
- لازم **اللقب المافياوي يظهر بشكل متكرر** في القصة.  
- لازم **القدرة الخارقة تلعب دور أساسي في الأحداث**.  
- اجعل القصة تنتهي بلمحة عن **توقعات العضو من السيرفر**.  
";

            try
            {
                var openAiKey = Environment.GetEnvironmentVariable("OPENAI_KEY");
                if (string.IsNullOrEmpty(openAiKey))
                {
                    _logger.LogError("OpenAI API key not found");
                    return "حصلت مشكلة في إعدادات الـ AI.";
                }

                var client = new RestClient("https://api.openai.com/v1/chat/completions");
                var request = new RestRequest("", Method.Post);
                request.AddHeader("Authorization", $"Bearer {openAiKey}");
                request.AddHeader("Content-Type", "application/json");

                var body = new
                {
                    model = "gpt-4o-mini",
                    messages = new object[]
                    {
                        new { role = "user", content = prompt }
                    },
                    max_tokens = 800,
                    temperature = 1
                };

                request.AddJsonBody(body);

                var response = await client.ExecuteAsync(request);

                _logger.LogInformation("[OpenAI] HTTP Status: {StatusCode}", response.StatusCode);

                if (!response.IsSuccessful)
                {
                    _logger.LogError("[OpenAI] Failed with status: {StatusCode}", response.StatusCode);
                    return $"حصلت مشكلة أثناء توليد القصة (OpenAI). كود: {(int)response.StatusCode}";
                }

                var json = JObject.Parse(response.Content ?? "{}");
                var content = json["choices"]?[0]?["message"]?["content"]?.ToString()
                    ?? json["choices"]?[0]?["text"]?.ToString();

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning("[OpenAI] No content returned");
                    return "مافيش محتوى راجع من الـ AI.";
                }

                return content.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OpenAI Exception] Failed to generate story");
                return "حصل خطأ غير متوقع أثناء توليد القصة.";
            }
        }

        public async Task SendStoryToChannelAsync(SocketGuildUser user, string story, bool hasInvite, DiscordBotService discordService)
        {
            try
            {
                if (discordService == null)
                {
                    _logger.LogError("[Story] DiscordBotService is null - cannot send story to channel");
                    return;
                }

                var storyChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");
                if (string.IsNullOrEmpty(storyChannelIdStr) || !ulong.TryParse(storyChannelIdStr, out var storyChannelId))
                {
                    _logger.LogError("[Story] Story channel ID not found in environment variables");
                    return;
                }

                var client = discordService.GetClient();
                if (client == null)
                {
                    _logger.LogError("[Story] Discord client is null - cannot send story to channel");
                    return;
                }

                var storyChannel = client.GetChannel(storyChannelId) as IMessageChannel;

                if (storyChannel == null)
                {
                    _logger.LogError("[Story] Story channel not found: {ChannelId}", storyChannelId);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"🎭 {user.Username} - قصة جديدة!")
                    .WithDescription(story)
                    .WithColor(hasInvite ? Color.Green : Color.Orange)
                    .WithThumbnailUrl(user.GetAvatarUrl() ?? "")
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .WithFooter(footer => footer.Text = $"UserID: {user.Id} | {(hasInvite ? "🟢 عضو بإنفايت" : "🟠 عضو بدون إنفايت")}")
                    .Build();

                await storyChannel.SendMessageAsync(text: user.Mention, embed: embed);
                _logger.LogInformation("[Story] Story sent to channel successfully for user {Username}", user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to send story to channel for user {Username}", user.Username);
            }
        }

        public void SaveStory(ulong userId, string story)
        {
            try
            {
                var stories = File.Exists(StoriesFile)
                    ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile)) ?? new Dictionary<ulong, string>()
                    : new Dictionary<ulong, string>();

                stories[userId] = story;
                File.WriteAllText(StoriesFile, JsonConvert.SerializeObject(stories, Formatting.Indented));
                _logger.LogInformation("[Story] Story saved for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to save story");
            }
        }

        public string LoadStory(ulong userId)
        {
            try
            {
                if (!File.Exists(StoriesFile)) return "";
                var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile)) ?? new Dictionary<ulong, string>();
                return stories.ContainsKey(userId) ? stories[userId] : "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to load story");
                return "";
            }
        }
    }
}
