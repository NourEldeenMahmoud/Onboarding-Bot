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
أنت كاتب قصص Roleplay لعالم المافيا في سيرفر Discord اسمه ""BitMob""، 
المدينة اسمها ""The Underworld""، مليئة بالأماكن: Coding Alley، Debuggers Street، 
The Underworld Casino، Don's Office، The Underworld Academy، Police HQ، Black Market، 
Hidden Docks، Tech Lab، Abandoned Warehouse، والمزيد.

السيرفر يحتوي على الرولز التالية مع الطبقية:
- The Don: رأس العصابة، أعلى سلطة.
- Consigliere: المستشار المقرب للـ Don.
- The Don's Kin: أفراد العائلة المقربة للـ Don.
- Associate: أعضاء معتمدين في العصابة.
- Outsider: أشخاص خارج العصابة لكن لهم تواصل محدود.
- Shady Snitch: العملاء أو المخبرين اللي عندهم معلومات سرية، هذا **رول وليس اسم شخص**.

مهمتك: ابتكر قصة قصيرة وحماسية لهذا العضو الجديد بحيث تكون **ديناميكية ومليئة بالمصادفات والأحداث المافياوية**، مع **لمسة هزلية/ساخرة** خفيفة.  
ركز على العضو الجديد والشخص الذي دعاه، واجعل القصة مختلفة عن أي قصة سابقة.

المطلوب في القصة:
1. الاسم المستعار بأسلوب مافياوي، مرتبط بصفات العضو أو مهارته، ويكون مستوحى أو قريب من اسمه الحقيقي.
2. خلفية درامية: العضو لم ينضم بسهولة، بل حدث موقف مثير أو اكتشافه بواسطة أحد رجال العصابة أو مهمة خطرة أو مطاردة أو تحدي في المدينة.
3. دمج الشخص الذي دعاه، الرول الخاص به، وقصته السابقة داخل أحداث القصة بشكل مباشر.
4. الأحداث يجب أن تكون مشوقة، مليئة بمصطلحات المافيا: عمليات سرية، تهديدات، ملاحقات، اختراقات، لقاءات مع رجال العصابة، مخططات.
5. أضف لمسات برمجية بسيطة: العضو قد يستخدم مهارات برمجية أو اختراقية لإنقاذ الموقف أو اكتشاف سر، لكن **الأحداث المافياوية تظل محور القصة**.
6. النهاية: العضو يتم قبوله في العصابة ويحصل على مكانه الأولي في المدينة بناءً على الرولز، ويكون دوره واضح في المكان الذي يليق بمهاراته.
7. القصة يجب أن تكون **قصيرة، ممتعة، مليئة بالمفاجآت، وذات طابع هزلي خفيف**، مع الحفاظ على الجو المافيوي.

معلومات العضو الجديد:
- الاسم: {userResponses["name"]}
- السن: {userResponses["age"]}
- الاهتمامات: {userResponses["interest"]}
- التخصص: {userResponses["specialty"]}
- الميزة: {userResponses["strength"]}
- العيب: {userResponses["weakness"]}
- المكان المفضل: {userResponses["favoritePlace"]}

معلومات الشخص الذي دعاه:
- الاسم المستعار: {inviterName}
- الرول: {inviterRole}
- قصته السابقة: {inviterStory}
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
                    temperature = 0.7
                };

                request.AddJsonBody(body);

                var response = await client.ExecuteAsync(request);

                _logger.LogInformation("[OpenAI] HTTP Status: {StatusCode}", response.StatusCode);

                if (!response.IsSuccessful)
                {
                    _logger.LogError("[OpenAI] Failed with status: {StatusCode}", response.StatusCode);
                    return $"حصلت مشكلة أثناء توليد القصة (OpenAI). كود: {(int)response.StatusCode}";
                }

                var json = JObject.Parse(response.Content);
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
                var storyChannelIdStr = Environment.GetEnvironmentVariable("DISCORD_STORY_CHANNEL_ID");
                if (string.IsNullOrEmpty(storyChannelIdStr) || !ulong.TryParse(storyChannelIdStr, out var storyChannelId))
                {
                    _logger.LogError("Story channel ID not found in environment variables");
                    return;
                }

                var storyChannel = discordService.GetClient().GetChannel(storyChannelId) as IMessageChannel;

                if (storyChannel == null)
                {
                    _logger.LogError("Story channel not found");
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"🎭 {user.Username} - قصة جديدة!")
                    .WithDescription(story)
                    .WithColor(hasInvite ? Color.Green : Color.Orange)
                    .WithThumbnailUrl(user.GetAvatarUrl())
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .WithFooter(footer => footer.Text = hasInvite ? "🟢 عضو بإنفايت" : "🟠 عضو بدون إنفايت")
                    .Build();

                await storyChannel.SendMessageAsync(embed: embed);
                _logger.LogInformation("[Story] Story sent to channel successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Error] Failed to send story to channel");
            }
        }

        public void SaveStory(ulong userId, string story)
        {
            try
            {
                var stories = File.Exists(StoriesFile)
                    ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile))
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
                var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile));
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
