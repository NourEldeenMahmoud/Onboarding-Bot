using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord.Rest;
using DotNetEnv;
using System.Net;
using System.Text;

class Program
{
    private DiscordSocketClient? _client;
    private InteractionService? _interactionService;

    string _token = "";
    string _ChatGPTApiKey = "";
    
    // Configuration - Load from environment variables for security
    private ulong familyStoriesChannelId;
    private ulong cityGatesChannelId; // قناة City Gates للأسئلة
    private ulong ownerId; // ID الـ Owner
    private ulong logChannelId;
    private ulong associateRoleId;
    private ulong outsiderRoleId;
    private const string StoriesFile = "stories.json";
    private const string InviteHistoryFile = "invite_history.json";

    static async Task Main(string[] args)
    {
        var program = new Program();
        await program.StartBotAsync();
    }

    public async Task StartBotAsync()
    {
        try
        {
            Console.WriteLine("[Bot] Starting Discord bot...");
            
            // Start HTTP server for Render
            _ = Task.Run(() => StartHttpServer());
            
            await MainAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bot Error] {ex}");
        }
    }

    private async Task StartHttpServer()
    {
        try
        {
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            Console.WriteLine($"[HTTP Server] Starting simple HTTP server on port {port}");
            
            // Simple HTTP server using TcpListener
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, int.Parse(port));
            listener.Start();
            
            Console.WriteLine($"[HTTP Server] Listening on port {port}");
            
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleTcpRequest(client));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HTTP Server Error] {ex}");
        }
    }

    private async Task HandleTcpRequest(System.Net.Sockets.TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            
            var request = await reader.ReadLineAsync();
            if (request == null) return;
            
            var parts = request.Split(' ');
            if (parts.Length < 2) return;
            
            var method = parts[0];
            var path = parts[1];
            
            string content;
            string contentType = "text/plain";
            
            switch (path)
            {
                case "/":
                    content = "🎭 BitMob Bot is running! The Underworld awaits... 🌃";
                    break;
                    
                case "/health":
                    content = "✅ Bot is healthy and ready!";
                    break;
                    
                case "/status":
                    var status = new
                    {
                        status = "online",
                        timestamp = DateTime.UtcNow,
                        service = "BitMob Discord Bot",
                        version = "1.0.0"
                    };
                    content = JsonConvert.SerializeObject(status);
                    contentType = "application/json";
                    break;
                    
                default:
                    content = "404 - Not Found";
                    break;
            }
            
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(content)}\r\n\r\n{content}";
            await writer.WriteAsync(response);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HTTP Request Error] {ex}");
        }
        finally
        {
            client.Close();
        }
    }

    public async Task MainAsync()
    {
        Env.Load();

        _token = Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "";
        _ChatGPTApiKey = Environment.GetEnvironmentVariable("OPENAI_KEY") ?? "";
        
        // Load configuration from environment variables
        LoadConfiguration();

        if (string.IsNullOrEmpty(_token))
        {
            Console.WriteLine("[Bot Error] DISCORD_TOKEN not found in environment variables");
            return;
        }

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.All
        });

        // Initialize Interaction Service
        _interactionService = new InteractionService(_client.Rest);

        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        // إزالة UserJoined event - النظام الجديد يعتمد على /start command
        // _client.UserJoined += async (user) =>
        // {
        //     _ = Task.Run(() => HandleUserJoinedAsync(user));
        // };

        // Add interaction handler
        _client.InteractionCreated += HandleInteractionAsync;

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();

        Console.WriteLine("[Bot] Bot started successfully and running in background");
        
        // الحفاظ على البوت يعمل
        await Task.Delay(-1);
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine($"[Discord.NET] {log}");
        return Task.CompletedTask;
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            if (_client == null || _interactionService == null)
            {
                Console.WriteLine("[Error] Client or InteractionService not initialized");
                return;
            }

            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, null);
            
            if (!result.IsSuccess)
            {
                Console.WriteLine($"[Interaction Error] {result.ErrorReason}");
                await LogError("Interaction Error", result.ErrorReason, $"Failed to execute command: Unknown");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Interaction Error] {ex}");
            await LogError("Interaction Error", ex.ToString(), "Failed to handle interaction");
        }
    }

    private async Task RegisterCommandsAsync()
    {
        try
        {
            if (_interactionService == null || _client == null)
            {
                Console.WriteLine("[Error] Client or InteractionService not initialized");
                return;
            }

            // Register global commands
            await _interactionService.AddModuleAsync<StoryCommands>(null);
            
            foreach (var guild in _client.Guilds)
            {
                await _interactionService.RegisterCommandsToGuildAsync(guild.Id);
            }
            
            Console.WriteLine("[Info] Slash commands registered successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to register commands: {ex}");
            await LogError("Command Registration Error", ex.ToString(), "Failed to register slash commands");
        }
    }

    private async Task ReadyAsync()
    {
        if (_client?.CurrentUser != null)
        {
            Console.WriteLine($"[Ready] {_client.CurrentUser} is connected!");
        }

        // تسجيل الأوامر بعد الاتصال
        await RegisterCommandsAsync();
    }



    private async Task AssignRole(SocketGuildUser user, ulong roleId, string roleName)
    {
        try
        {
            if (roleId == 0)
            {
                Console.WriteLine($"[Warning] Role ID for {roleName} is not configured. Please set the role ID in the constants.");
                return;
            }

            var role = user.Guild?.GetRole(roleId);
            if (role != null)
            {
                await user.AddRoleAsync(role);
                Console.WriteLine($"[Info] Successfully assigned {roleName} role to {user.Username}");
            }
            else
            {
                Console.WriteLine($"[Error] Role {roleName} not found in the server");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to assign {roleName} role to {user.Username}: {ex.Message}");
            await LogError("Role Assignment Error", ex.Message, $"Failed to assign {roleName} role to {user.Username}");
        }
    }







    private async Task LogError(string errorType, string errorMessage, string context = "")
    {
        try
        {
            if (logChannelId == 0 || _client == null)
                return;

            var logChannel = _client?.GetChannel(logChannelId) as IMessageChannel;
            if (logChannel == null)
            {
                Console.WriteLine($"[Warning] Log channel with ID {logChannelId} not found.");
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logMessage = $"🚨 **خطأ في البوت** - {timestamp}\n" +
                           $"**نوع الخطأ:** {errorType}\n" +
                           $"**السياق:** {context}\n" +
                           $"**تفاصيل الخطأ:**\n```\n{errorMessage}\n```";

            await SendMessageSafe(logChannel, logMessage);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to log error to channel: {ex.Message}");
        }
    }

    private async Task SendMessageSafe(IMessageChannel channel, string message)
    {
        try
        {
            const int MaxLength = 2000;

            if (string.IsNullOrEmpty(message))
                return;

            // لو الرسالة أقل من الحد، بعتها عادي
            if (message.Length <= MaxLength)
            {
                await channel.SendMessageAsync(message);
                return;
            }

            // تجزئة الرسالة على أجزاء ≤ 2000 حرف
            for (int i = 0; i < message.Length; i += MaxLength)
            {
                var chunk = message.Substring(i, Math.Min(MaxLength, message.Length - i));
                await channel.SendMessageAsync(chunk);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Channel Error] " + ex.Message);
            await LogError("Channel Message Error", ex.Message, "Failed to send message to channel");
        }
    }









    private string ExtractStoryTitle(string story)
    {
        // محاولة استخراج عنوان القصة من أول سطر
        var lines = story.Split('\n');
        if (lines.Length > 0)
        {
            var firstLine = lines[0].Trim();
            // إزالة العلامات مثل ** أو # أو أي تنسيق
            firstLine = firstLine.Replace("**", "").Replace("#", "").Replace("*", "").Trim();
            if (firstLine.Length > 0 && firstLine.Length <= 100)
            {
                return firstLine;
            }
        }
        return "قصة العضو";
    }



    private async Task<string> GenerateStory(
        string name, string age, string interest, string specialty,
        string strength, string weakness, string place,
        string inviterName, string inviterRole, string inviterStory, bool hasInviter)
    {
        string prompt;
        
        if (hasInviter)
        {
            prompt = $@"
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
- الاسم: {name}
- السن: {age}
- الاهتمامات: {interest}
- التخصص: {specialty}
- الميزة: {strength}
- العيب: {weakness}
- المكان المفضل: {place}

معلومات الشخص الذي دعاه:
- الاسم المستعار: {inviterName}
- الرول: {inviterRole}
- قصته السابقة: {inviterStory}
";
        }
        else
        {
            prompt = $@"
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

مهمتك: ابتكر قصة لهذا المستخدم المجهول الذي انضم بدون دعوة. بما أنه ما عندوش مدعو، اكتب قصة غامضة عن كيفية اكتشافه للسيرفر أو العثور عليه من قبل العائلة.

المطلوب في القصة:
1. الاسم المستعار بأسلوب مافياوي، مرتبط بصفات العضو أو مهارته، ويكون مستوحى أو قريب من اسمه الحقيقي.
2. خلفية غامضة: كيف اكتشف هذا الشخص المجهول السيرفر؟ هل تم العثور عليه من قبل أفراد العائلة؟ هل تعثر في شيء سري؟
3. القصة يجب أن تكون أكثر غموضاً وحذراً بما أنه ما عندهوش صلة معروفة.
4. الأحداث يجب أن تكون مشوقة، مليئة بمصطلحات المافيا: عمليات سرية، تهديدات، ملاحقات، اختراقات، لقاءات مع رجال العصابة، مخططات.
5. أضف لمسات برمجية بسيطة: العضو قد يستخدم مهارات برمجية أو اختراقية لإنقاذ الموقف أو اكتشاف سر، لكن **الأحداث المافياوية تظل محور القصة**.
6. النهاية: العضو يتم إعطاؤه فرصة لإثبات نفسه لكن يبدأ كـ Outsider بسبب عدم وجود دعوة.
7. القصة يجب أن تكون **قصيرة، ممتعة، مليئة بالمفاجآت، وذات طابع هزلي خفيف**، مع الحفاظ على الجو المافيوي.

معلومات العضو المجهول:
- الاسم: {name}
- السن: {age}
- الاهتمامات: {interest}
- التخصص: {specialty}
- الميزة: {strength}
- العيب: {weakness}
- المكان المفضل: {place}

ملاحظة: هذا المستخدم انضم بدون دعوة، لذا يعتبر مجهول وغامض.
";
        }

        try
        {
            var client = new RestClient("https://api.openai.com/v1/chat/completions");
            var request = new RestRequest("", Method.Post);
            request.AddHeader("Authorization", $"Bearer {_ChatGPTApiKey}");
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

            Console.WriteLine("[OpenAI] HTTP Status: " + response.StatusCode);
            Console.WriteLine("[OpenAI] Response content: " + response.Content);

            if (!response.IsSuccessful)
            {
                return $"حصلت مشكلة أثناء توليد القصة (OpenAI). كود: {(int)response.StatusCode}";
            }

            var json = JObject.Parse(response.Content);
            var content = json["choices"]?[0]?["message"]?["content"]?.ToString()
                ?? json["choices"]?[0]?["text"]?.ToString();

            if (string.IsNullOrWhiteSpace(content))
                return "مافيش محتوى راجع من الـ AI.";

            return content.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[OpenAI Exception] " + ex);
            await LogError("OpenAI API Error", ex.ToString(), "Failed to generate story using OpenAI");
            return "حصل خطأ غير متوقع أثناء توليد القصة.";
        }
    }

    private void SaveStory(ulong userId, string story)
    {
        try
        {
            var stories = File.Exists(StoriesFile)
                ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile))
                : new Dictionary<ulong, string>();

            if (stories == null)
                stories = new Dictionary<ulong, string>();

            stories[userId] = story;
            File.WriteAllText(StoriesFile, JsonConvert.SerializeObject(stories, Formatting.Indented));
            Console.WriteLine($"[SaveStory] Story saved for user {userId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SaveStory Error] {ex.Message}");
        }
    }

    private void DeleteStory(ulong userId)
    {
        try
        {
            if (!File.Exists(StoriesFile)) return;
            
            var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile));
            if (stories == null) return;

            if (stories.Remove(userId))
            {
                File.WriteAllText(StoriesFile, JsonConvert.SerializeObject(stories, Formatting.Indented));
                Console.WriteLine($"[DeleteStory] Story deleted for user {userId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeleteStory Error] {ex.Message}");
        }
    }

    private string LoadStory(ulong userId)
    {
        try
        {
            if (!File.Exists(StoriesFile)) return "";
            var jsonContent = File.ReadAllText(StoriesFile);
            if (string.IsNullOrEmpty(jsonContent)) return "";
            var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(jsonContent) ?? new Dictionary<ulong, string>();
            return stories.ContainsKey(userId) ? stories[userId] : "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoadStory Error] {ex.Message}");
            return "";
        }
    }

    private void SaveInviteHistory(ulong userId, string inviterName, ulong inviterId, string inviteCode, DateTime joinDate)
    {
        try
        {
            var inviteHistory = File.Exists(InviteHistoryFile)
                ? JsonConvert.DeserializeObject<Dictionary<ulong, InviteInfo>>(File.ReadAllText(InviteHistoryFile))
                : new Dictionary<ulong, InviteInfo>();

            inviteHistory[userId] = new InviteInfo
            {
                InviterName = inviterName,
                InviterId = inviterId,
                InviteCode = inviteCode,
                JoinDate = joinDate
            };

            File.WriteAllText(InviteHistoryFile, JsonConvert.SerializeObject(inviteHistory, Formatting.Indented));
            Console.WriteLine($"[Invite History] Saved invite info for user {userId}: {inviterName} via {inviteCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SaveInviteHistory Error] {ex}");
        }
    }

    private InviteInfo LoadInviteHistory(ulong userId)
    {
        try
        {
            if (!File.Exists(InviteHistoryFile)) return null;
            
            var jsonContent = File.ReadAllText(InviteHistoryFile);
            if (string.IsNullOrEmpty(jsonContent)) return null;
            
            var inviteHistory = JsonConvert.DeserializeObject<Dictionary<ulong, InviteInfo>>(jsonContent) ?? new Dictionary<ulong, InviteInfo>();
            return inviteHistory.ContainsKey(userId) ? inviteHistory[userId] : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoadInviteHistory Error] {ex}");
            return null;
        }
    }







    private void LoadConfiguration()
    {
        try
        {
            // Load channel and role IDs from environment variables
            var familyStoriesChannelIdStr = Environment.GetEnvironmentVariable("FAMILY_STORIES_CHANNEL_ID");
            var cityGatesChannelIdStr = Environment.GetEnvironmentVariable("CITY_GATES_CHANNEL_ID");
            var ownerIdStr = Environment.GetEnvironmentVariable("OWNER_ID");
            var logChannelIdStr = Environment.GetEnvironmentVariable("LOG_CHANNEL_ID");
            var associateRoleIdStr = Environment.GetEnvironmentVariable("ASSOCIATE_ROLE_ID");
            var outsiderRoleIdStr = Environment.GetEnvironmentVariable("OUTSIDER_ROLE_ID");

            if (ulong.TryParse(familyStoriesChannelIdStr, out ulong familyStoriesId))
            {
                familyStoriesChannelId = familyStoriesId;
                Console.WriteLine($"[Config] Family Stories channel ID loaded: {familyStoriesChannelId}");
            }
            else
            {
                Console.WriteLine("[Config Warning] FAMILY_STORIES_CHANNEL_ID not found or invalid. Stories won't be posted to channels.");
                familyStoriesChannelId = 0;
            }

            if (ulong.TryParse(cityGatesChannelIdStr, out ulong cityGatesId))
            {
                cityGatesChannelId = cityGatesId;
                Console.WriteLine($"[Config] City Gates channel ID loaded: {cityGatesChannelId}");
            }
            else
            {
                Console.WriteLine("[Config Error] CITY_GATES_CHANNEL_ID not found or invalid. Onboarding won't work without this!");
                cityGatesChannelId = 0;
            }

            if (ulong.TryParse(ownerIdStr, out ulong ownerIdValue))
            {
                ownerId = ownerIdValue;
                Console.WriteLine($"[Config] Owner ID loaded: {ownerId}");
            }
            else
            {
                Console.WriteLine("[Config Error] OWNER_ID not found or invalid. Message permissions won't work properly!");
                ownerId = 0;
            }

            if (ulong.TryParse(logChannelIdStr, out ulong logId))
            {
                logChannelId = logId;
                Console.WriteLine($"[Config] Log channel ID loaded: {logChannelId}");
            }
            else
            {
                Console.WriteLine("[Config Warning] LOG_CHANNEL_ID not found or invalid. Errors won't be logged to channels.");
                logChannelId = 0;
            }

            if (ulong.TryParse(associateRoleIdStr, out ulong associateId))
            {
                associateRoleId = associateId;
                Console.WriteLine($"[Config] Associate role ID loaded: {associateRoleId}");
            }
            else
            {
                Console.WriteLine("[Config Warning] ASSOCIATE_ROLE_ID not found or invalid. Associate role assignment will be skipped.");
                associateRoleId = 0;
            }

            if (ulong.TryParse(outsiderRoleIdStr, out ulong outsiderId))
            {
                outsiderRoleId = outsiderId;
                Console.WriteLine($"[Config] Outsider role ID loaded: {outsiderRoleId}");
            }
            else
            {
                Console.WriteLine("[Config Warning] OUTSIDER_ROLE_ID not found or invalid. Outsider role assignment will be skipped.");
                outsiderRoleId = 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config Error] Failed to load configuration: {ex.Message}");
        }
    }
}

// Story Commands Module
public class StoryCommands : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("story", "عرض قصة عضو معين")]
    public async Task ShowStory([Summary("user", "العضو الذي تريد عرض قصته")] SocketUser user)
    {
        try
        {
            await DeferAsync();

            // البحث عن قصة العضو في قناة القصص
            var story = await LoadStoryAsync(user.Id);
            
            if (string.IsNullOrEmpty(story))
            {
                await FollowupAsync($"❌ لا توجد قصة لـ {user.Mention} في قناة القصص");
                return;
            }

            // إنشاء Embed منسق للقصة
            var storyEmbed = new EmbedBuilder()
                .WithColor(new Color(0x00ff00))
                .WithAuthor("📜🎭 قصة العضو", iconUrl: user.GetAvatarUrl())
                .WithTitle(ExtractStoryTitle(story))
                .WithDescription(story)
                .Build();

            await FollowupAsync(embed: storyEmbed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Story Command Error] {ex}");
            Console.WriteLine($"[Error] Story Command Error: {ex.Message}");
            
            await FollowupAsync("❌ حدث خطأ أثناء عرض القصة");
        }
    }

    private async Task<string> LoadStoryAsync(ulong userId)
    {
        try
        {
            // الحصول على معرف قناة القصص من المتغيرات البيئية
            var familyStoriesChannelIdStr = Environment.GetEnvironmentVariable("FAMILY_STORIES_CHANNEL_ID");
            if (!ulong.TryParse(familyStoriesChannelIdStr, out ulong familyStoriesChannelId) || familyStoriesChannelId == 0)
            {
                return "";
            }
            
            var familyStoriesChannel = Context.Client.GetChannel(familyStoriesChannelId) as IMessageChannel;
            if (familyStoriesChannel == null) return "";

            Console.WriteLine($"[Story Load] Searching for story of user {userId} in story channel...");
            
            // البحث في آخر 100 رسالة في قناة القصص
            var messages = await familyStoriesChannel.GetMessagesAsync(100).FlattenAsync();
            
            foreach (var message in messages)
            {
                // التحقق من وجود منشن للعضو في الرسالة
                if (message.MentionedUserIds.Contains(userId))
                {
                    // البحث عن الـ Embed الذي يحتوي على القصة
                    foreach (var embed in message.Embeds)
                    {
                        if (!string.IsNullOrEmpty(embed.Description))
                        {
                            Console.WriteLine($"[Story Load] Found story for user {userId}");
                            return embed.Description;
                        }
                    }
                }
                
                // التحقق من وجود منشن للعضو في الـ Embeds
                foreach (var embed in message.Embeds)
                {
                    // البحث في محتوى الـ Embed عن منشن العضو
                    if (!string.IsNullOrEmpty(embed.Description) && embed.Description.Contains($"<@{userId}>"))
                    {
                        Console.WriteLine($"[Story Load] Found story for user {userId} in embed description");
                        return embed.Description;
                    }
                }
            }
            
            Console.WriteLine($"[Story Load] No story found for user {userId}");
            return "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Story Load Error] {ex.Message}");
            return "";
        }
    }

    private string ExtractStoryTitle(string story)
    {
        // محاولة استخراج عنوان القصة من أول سطر
        var lines = story.Split('\n');
        if (lines.Length > 0)
        {
            var firstLine = lines[0].Trim();
            // إزالة العلامات مثل ** أو # أو أي تنسيق
            firstLine = firstLine.Replace("**", "").Replace("#", "").Replace("*", "").Trim();
            if (firstLine.Length > 0 && firstLine.Length <= 100)
            {
                return firstLine;
            }
        }
        return "قصة العضو";
    }

    [SlashCommand("invite", "عرض معلومات الدعوة لعضو معين")]
    public async Task ShowInviteInfo([Summary("user", "العضو الذي تريد معرفة من دعاه")] SocketUser user)
    {
        try
        {
            await DeferAsync();

            var guild = Context.Guild;
            if (guild == null)
            {
                await FollowupAsync("❌ هذا الأمر متاح فقط في السيرفرات");
                return;
            }

            // Get user's join date
            var guildUser = guild.GetUser(user.Id);
            if (guildUser == null)
            {
                await FollowupAsync($"❌ {user.Mention} غير موجود في هذا السيرفر");
                return;
            }

            var joinDate = guildUser.JoinedAt?.ToString("dd/MM/yyyy HH:mm") ?? "غير معروف";
            
            // Try to get invite information from saved history first
            string inviterInfo = "غير معروف";
            string inviteCode = "غير معروف";
            
            var inviteHistory = LoadInviteHistory(user.Id);
            if (inviteHistory != null)
            {
                inviterInfo = inviteHistory.InviterName;
                inviteCode = inviteHistory.InviteCode;
            }
            else
            {
                // لا توجد معلومات محفوظة عن الدعوة
                inviterInfo = "غير معروف (لا توجد معلومات محفوظة)";
            }

            var embed = new EmbedBuilder()
                .WithColor(0x00ff00)
                .WithTitle($"📋 معلومات انضمام {user.Username}")
                .AddField("👤 العضو", user.Mention, true)
                .AddField("📅 تاريخ الانضمام", joinDate, true)
                .AddField("🤝 من دعاه", inviterInfo, true)
                .AddField("🔗 كود الدعوة", inviteCode, true)
                .WithThumbnailUrl(user.GetAvatarUrl())
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await FollowupAsync(embed: embed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Invite Command Error] {ex}");
            Console.WriteLine($"[Error] Invite Command Error: {ex.Message}");
            
            await FollowupAsync("❌ حدث خطأ أثناء عرض معلومات الدعوة");
        }
    }

    [SlashCommand("deletestory", "حذف قصة عضو معين")]
    public async Task DeleteStory([Summary("user", "العضو الذي تريد حذف قصته")] SocketUser user)
    {
        try
        {
            await DeferAsync();

            // حذف القصة من الملف
            DeleteStoryFromFile(user.Id);
            
            var embed = new EmbedBuilder()
                .WithColor(0xff6b35)
                .WithTitle("🗑️ تم حذف القصة")
                .WithDescription($"تم حذف قصة {user.Mention} من قاعدة البيانات.\n\n" +
                               "**ملاحظة:** إذا كانت القصة موجودة في قناة القصص، يجب حذفها يدوياً من هناك أيضاً.")
                .WithFooter("استخدم الأمر مرة أخرى إذا أردت إعادة إنشاء القصة")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await FollowupAsync(embed: embed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Delete Story Command Error] {ex}");
            Console.WriteLine($"[Error] Delete Story Command Error: {ex.Message}");
            
            await FollowupAsync("❌ حدث خطأ أثناء حذف القصة");
        }
    }

    [SlashCommand("join", "بدء عملية الانضمام للعائلة")]
    public async Task StartOnboarding()
    {
        try
        {
            await DeferAsync();

            var user = Context.User as SocketGuildUser;
            if (user == null)
            {
                await FollowupAsync("❌ حدث خطأ في تحديد العضو");
                return;
            }

            // التحقق من أن الأمر مستخدم في قناة الأسئلة
            var cityGatesChannelIdStr = Environment.GetEnvironmentVariable("CITY_GATES_CHANNEL_ID");
            if (ulong.TryParse(cityGatesChannelIdStr, out ulong requiredChannelId))
            {
                if (Context.Channel.Id != requiredChannelId)
                {
                    var cityGatesChannel = Context.Guild.GetTextChannel(requiredChannelId);
                    var channelMention = cityGatesChannel != null ? cityGatesChannel.Mention : "#city-gates";
                    
                    await FollowupAsync($"❌ **هذا الأمر يعمل فقط في {channelMention}**\n\n" +
                                      "🔍 اذهب إلى قناة الأسئلة واستخدم الأمر هناك.");
                    return;
                }
            }

            // التحقق من وجود قصة للعضو
            bool hasStory = await CheckStoryInChannel(user.Id);
            
            if (hasStory)
            {
                // العضو له قصة - رسالة عضو قديم
                var embed = new EmbedBuilder()
                    .WithColor(0x2f3136)
                    .WithTitle("🎭 أهلاً بعودتك!")
                    .WithDescription("أنت عضو قديم في العائلة وقد أكملت عملية الانضمام من قبل.\n\n" +
                                   "**لا حاجة لإعادة التسجيل.** 🎉")
                    .WithFooter("مرحباً بك مرة أخرى في عائلة BitMob")
                    .WithTimestamp(DateTimeOffset.Now)
                    .Build();

                            // إرسال الرسالة في قناة القصص
            var familyStoriesChannelIdStr = Environment.GetEnvironmentVariable("FAMILY_STORIES_CHANNEL_ID");
            if (ulong.TryParse(familyStoriesChannelIdStr, out ulong familyStoriesChannelId) && familyStoriesChannelId != 0)
            {
                var familyStoriesChannel = Context.Guild.GetTextChannel(familyStoriesChannelId);
                if (familyStoriesChannel != null)
                {
                    await familyStoriesChannel.SendMessageAsync(text: user.Mention, embed: embed);
                }
            }
                
                await FollowupAsync("✅ تم إرسال رسالة الترحيب في قناة القصص");
                return;
            }

            // العضو جديد - بدء الـ onboarding
            await FollowupAsync("🎭 **مرحباً بك في عائلة BitMob!**\n\n" +
                              "سيتم بدء عملية الانضمام الآن...\n" +
                              "⚠️ **مهم:** عليك الانتظار في قناة الأسئلة لبدء المقابلة.");
            
            // استخدام نفس المتغير المعرف سابقاً
            if (ulong.TryParse(cityGatesChannelIdStr, out ulong cityGatesChannelId) && cityGatesChannelId != 0)
            {
                var cityGatesChannel = Context.Guild.GetTextChannel(cityGatesChannelId);
                if (cityGatesChannel != null)
                {
                    // إعطاء صلاحيات للعضو
                    await GiveUserChannelPermission(cityGatesChannel, user);
                    
                                    // إرسال رسالة بدء للعضو في قناة الأسئلة
                var welcomeEmbed = new EmbedBuilder()
                    .WithColor(0x2f3136)
                    .WithTitle("🎭 أهلاً وسهلاً بك!")
                    .WithDescription($"مرحباً بك {user.Mention} في عائلة BitMob!\n\n" +
                                   "سأطرح عليك بعض الأسئلة لكتابة قصتك.\n" +
                                   "**اكتب إجاباتك في هذه القناة.**")
                    .WithFooter("🔒 هذه المحادثة مرئية فقط لك وللإدارة")
                    .WithTimestamp(DateTimeOffset.Now)
                    .Build();
                
                await cityGatesChannel.SendMessageAsync(text: user.Mention, embed: welcomeEmbed);
                
                // بدء الأسئلة بعد تأخير قصير
                _ = Task.Delay(3000).ContinueWith(async _ => {
                    await StartOnboardingQuestions(cityGatesChannel, user);
                });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Start Command Error] {ex}");
            Console.WriteLine($"[Error] Join Command Error: {ex.Message}");
            
            await FollowupAsync("❌ حدث خطأ أثناء بدء عملية الانضمام");
        }
    }
    
    private async Task<bool> CheckStoryInChannel(ulong userId)
    {
        try
        {
            var familyStoriesChannelIdStr = Environment.GetEnvironmentVariable("FAMILY_STORIES_CHANNEL_ID");
            if (!ulong.TryParse(familyStoriesChannelIdStr, out ulong familyStoriesChannelId) || familyStoriesChannelId == 0)
            {
                return false;
            }
            
            var familyStoriesChannel = Context.Client.GetChannel(familyStoriesChannelId) as IMessageChannel;
            if (familyStoriesChannel == null) return false;

            // البحث في آخر 200 رسالة
            var messages = await familyStoriesChannel.GetMessagesAsync(200).FlattenAsync();
            
            foreach (var message in messages)
            {
                // التحقق من وجود منشن للعضو
                if (message.MentionedUserIds.Contains(userId))
                {
                    return true;
                }
                
                // التحقق من الـ embeds
                if (message.Embeds?.Any() == true)
                {
                    foreach (var embed in message.Embeds)
                    {
                        if (embed.Description?.Contains($"<@{userId}>") == true)
                        {
                            return true;
                        }
                    }
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CheckStoryInChannel Error] {ex.Message}");
            return false;
        }
    }
    
    private async Task GiveUserChannelPermission(ITextChannel channel, SocketGuildUser user)
    {
        try
        {
            // إعطاء صلاحيات كاملة للعضو
            var permissionOverwrite = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                addReactions: PermValue.Allow,
                embedLinks: PermValue.Allow,
                attachFiles: PermValue.Allow,
                useExternalEmojis: PermValue.Allow
            );
            
            await channel.AddPermissionOverwriteAsync(user, permissionOverwrite);
            Console.WriteLine($"[Permission] Gave full channel permission to {user.Username}");
            
            // إعطاء صلاحيات للتفاعل مع الأعضاء الآخرين
            await EnableUserInteraction(channel, user);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Permission Error] {ex.Message}");
        }
    }

    private async Task EnableUserInteraction(ITextChannel channel, SocketGuildUser user)
    {
        try
        {
            // تبسيط الكود - إعطاء صلاحيات للتفاعل مع الأعضاء الآخرين
            var outsiderRoleIdStr = Environment.GetEnvironmentVariable("OUTSIDER_ROLE_ID");
            if (ulong.TryParse(outsiderRoleIdStr, out ulong outsiderRoleId) && outsiderRoleId != 0)
            {
                var outsiderRole = channel.Guild.GetRole(outsiderRoleId);
                if (outsiderRole != null)
                {
                    // إعطاء صلاحيات للتفاعل مع الأعضاء الآخرين
                    var interactionPermissions = new OverwritePermissions(
                        viewChannel: PermValue.Allow,
                        sendMessages: PermValue.Allow,
                        readMessageHistory: PermValue.Allow,
                        addReactions: PermValue.Allow,
                        embedLinks: PermValue.Allow,
                        attachFiles: PermValue.Allow,
                        useExternalEmojis: PermValue.Allow
                    );
                    
                    await channel.AddPermissionOverwriteAsync(user, interactionPermissions);
                    Console.WriteLine($"[Interaction] Enabled interaction for {user.Username}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Interaction Error] {ex.Message}");
        }
    }

    private async Task PromoteUserToAssociate(SocketGuildUser user)
    {
        try
        {
            var associateRoleIdStr = Environment.GetEnvironmentVariable("ASSOCIATE_ROLE_ID");
            var outsiderRoleIdStr = Environment.GetEnvironmentVariable("OUTSIDER_ROLE_ID");
            
            if (ulong.TryParse(associateRoleIdStr, out ulong associateRoleId) && associateRoleId != 0)
            {
                var associateRole = user.Guild.GetRole(associateRoleId);
                if (associateRole != null)
                {
                    await user.AddRoleAsync(associateRole);
                    Console.WriteLine($"[Promotion] Promoted {user.Username} to Associate role");
                }
            }
            
            if (ulong.TryParse(outsiderRoleIdStr, out ulong outsiderRoleId) && outsiderRoleId != 0)
            {
                var outsiderRole = user.Guild.GetRole(outsiderRoleId);
                if (outsiderRole != null)
                {
                    await user.RemoveRoleAsync(outsiderRole);
                    Console.WriteLine($"[Promotion] Removed Outsider role from {user.Username}");
                }
            }
            
            // تحديث صلاحيات القناة
            var cityGatesChannelIdStr = Environment.GetEnvironmentVariable("CITY_GATES_CHANNEL_ID");
            if (ulong.TryParse(cityGatesChannelIdStr, out ulong cityGatesChannelId) && cityGatesChannelId != 0)
            {
                var cityGatesChannel = user.Guild.GetTextChannel(cityGatesChannelId);
                if (cityGatesChannel != null)
                {
                    await SetChannelVisibilityForUser(cityGatesChannel, user, false); // false = ليس outsider
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PromoteUserToAssociate Error] {ex.Message}");
        }
    }

    private async Task SetChannelVisibilityForUser(ITextChannel channel, SocketGuildUser user, bool isOutsider)
    {
        try
        {
            if (isOutsider)
            {
                // العضو outsider - إعطاء صلاحيات كاملة للتفاعل
                var allowPermissions = new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    sendMessages: PermValue.Allow,
                    readMessageHistory: PermValue.Allow,
                    addReactions: PermValue.Allow,
                    embedLinks: PermValue.Allow,
                    attachFiles: PermValue.Allow,
                    useExternalEmojis: PermValue.Allow
                );
                await channel.AddPermissionOverwriteAsync(user, allowPermissions);
                Console.WriteLine($"[Permission] Granted full channel access to outsider: {user.Username}");
                
                // تمكين التفاعل مع الأعضاء الآخرين
                await EnableUserInteraction(channel, user);
            }
            else
            {
                // العضو associate - إخفاء قناة الأسئلة عنه
                var hidePermissions = new OverwritePermissions(
                    viewChannel: PermValue.Deny,
                    sendMessages: PermValue.Deny,
                    readMessageHistory: PermValue.Deny,
                    addReactions: PermValue.Deny,
                    embedLinks: PermValue.Deny,
                    attachFiles: PermValue.Deny,
                    useExternalEmojis: PermValue.Deny
                );
                await channel.AddPermissionOverwriteAsync(user, hidePermissions);
                Console.WriteLine($"[Permission] Hidden channel from associate: {user.Username}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SetChannelVisibility Error] {ex.Message}");
        }
    }

    [SlashCommand("updatepermissions", "تحديث صلاحيات قناة الأسئلة لجميع الأعضاء")]
    public async Task UpdateChannelPermissions()
    {
        try
        {
            await DeferAsync();
            
            // التحقق من أن المستخدم هو المالك أو أدمن
            var user = Context.User as SocketGuildUser;
            var ownerIdStr = Environment.GetEnvironmentVariable("OWNER_ID");
            
            bool isAuthorized = false;
            if (ulong.TryParse(ownerIdStr, out ulong ownerId) && user?.Id == ownerId)
            {
                isAuthorized = true;
            }
            else if (user?.GuildPermissions.Administrator == true)
            {
                isAuthorized = true;
            }
            
            if (!isAuthorized)
            {
                await FollowupAsync("❌ هذا الأمر متاح فقط للمالك والأدمن");
                return;
            }

            var cityGatesChannelIdStr = Environment.GetEnvironmentVariable("CITY_GATES_CHANNEL_ID");
            if (!ulong.TryParse(cityGatesChannelIdStr, out ulong cityGatesChannelId) || cityGatesChannelId == 0)
            {
                await FollowupAsync("❌ قناة الأسئلة غير معرفة في الإعدادات");
                return;
            }

            var cityGatesChannel = Context.Guild.GetTextChannel(cityGatesChannelId);
            if (cityGatesChannel == null)
            {
                await FollowupAsync("❌ قناة الأسئلة غير موجودة");
                return;
            }

            await FollowupAsync("⚙️ **جاري تحديث صلاحيات جميع الأعضاء...**");

            int totalUsers = 0;
            int completedUsers = 0;
            int incompleteUsers = 0;

            foreach (var guildUser in Context.Guild.Users)
            {
                if (guildUser.IsBot) continue;
                totalUsers++;

                // التحقق من رول العضو
                bool isOutsider = guildUser.Roles.Any(r => r.Id == ulong.Parse(Environment.GetEnvironmentVariable("OUTSIDER_ROLE_ID") ?? "0"));
                bool isAssociate = guildUser.Roles.Any(r => r.Id == ulong.Parse(Environment.GetEnvironmentVariable("ASSOCIATE_ROLE_ID") ?? "0"));
                
                if (isOutsider)
                {
                    incompleteUsers++;
                }
                else if (isAssociate)
                {
                    completedUsers++;
                }
                
                // تحديث الصلاحيات بناءً على الرول
                await SetChannelVisibilityForUser(cityGatesChannel, guildUser, isOutsider);
            }

            var summaryEmbed = new EmbedBuilder()
                .WithColor(0x00ff00)
                .WithTitle("✅ تم تحديث الصلاحيات بنجاح!")
                .WithDescription($"**إحصائيات:**\n" +
                               $"📊 إجمالي الأعضاء: {totalUsers}\n" +
                               $"🟢 Associate: {completedUsers} (مخفية عنهم قناة الأسئلة)\n" +
                               $"🟠 Outsider: {incompleteUsers} (مرئية لهم قناة الأسئلة)")
                .WithFooter("نظام الصلاحيات يعتمد على الرولات")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await FollowupAsync(embed: summaryEmbed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UpdateChannelPermissions Error] {ex}");
            await FollowupAsync("❌ حدث خطأ أثناء تحديث الصلاحيات");
        }
    }

    [SlashCommand("promote", "ترقية عضو إلى رول Associate (للإدارة فقط)")]
    public async Task PromoteUser([Summary("user", "العضو المراد ترقيته")] SocketUser targetUser)
    {
        try
        {
            await DeferAsync();
            
            // التحقق من أن المستخدم هو المالك أو أدمن
            var user = Context.User as SocketGuildUser;
            var ownerIdStr = Environment.GetEnvironmentVariable("OWNER_ID");
            
            bool isAuthorized = false;
            if (ulong.TryParse(ownerIdStr, out ulong ownerId) && user?.Id == ownerId)
            {
                isAuthorized = true;
            }
            else if (user?.GuildPermissions.Administrator == true)
            {
                isAuthorized = true;
            }
            
            if (!isAuthorized)
            {
                await FollowupAsync("❌ هذا الأمر متاح فقط للمالك والأدمن");
                return;
            }

            var targetGuildUser = targetUser as SocketGuildUser;
            if (targetGuildUser == null)
            {
                await FollowupAsync("❌ حدث خطأ في تحديد العضو");
                return;
            }

            // ترقية العضو
            await PromoteUserToAssociate(targetGuildUser);
            
            var embed = new EmbedBuilder()
                .WithColor(0x00ff00)
                .WithTitle("🎉 تمت الترقية بنجاح!")
                .WithDescription($"تم ترقية {targetUser.Mention} إلى رول **Associate**\\n\\n" +
                               "✅ تم إزالة رول Outsider\\n" +
                               "✅ تم إعطاء رول Associate\\n" +
                               "🔒 تم إخفاء قناة الأسئلة عنه")
                .WithFooter("العضو الآن عضو معتمد في العائلة")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await FollowupAsync(embed: embed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Promote Command Error] {ex}");
            await FollowupAsync("❌ حدث خطأ أثناء الترقية");
        }
    }

    private void DeleteStoryFromFile(ulong userId)
    {
        try
        {
            const string StoriesFile = "stories.json";
            if (!File.Exists(StoriesFile)) return;
            
            var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile));
            if (stories == null) return;

            if (stories.Remove(userId))
            {
                File.WriteAllText(StoriesFile, JsonConvert.SerializeObject(stories, Formatting.Indented));
                Console.WriteLine($"[DeleteStory] Story deleted for user {userId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeleteStoryFromFile Error] {ex.Message}");
        }
    }

    private InviteInfo LoadInviteHistory(ulong userId)
    {
        try
        {
            const string InviteHistoryFile = "invite_history.json";
            if (!File.Exists(InviteHistoryFile)) return null;
            
            var jsonContent = File.ReadAllText(InviteHistoryFile);
            if (string.IsNullOrEmpty(jsonContent)) return null;
            
            var inviteHistory = JsonConvert.DeserializeObject<Dictionary<ulong, InviteInfo>>(jsonContent) ?? new Dictionary<ulong, InviteInfo>();
            return inviteHistory.ContainsKey(userId) ? inviteHistory[userId] : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoadInviteHistory Error] {ex}");
            return null;
        }
    }

    private async Task StartOnboardingQuestions(ITextChannel channel, SocketGuildUser user)
    {
        try
        {
            // قائمة الأسئلة
            var questions = new[]
            {
                "ما اسمك الحقيقي؟",
                "كم عمرك؟",
                "ما هي اهتماماتك؟",
                "ما هو تخصصك؟",
                "ما هي ميزتك؟",
                "ما هو عيبك؟",
                "ما هو المكان المفضل لديك؟"
            };

            var answers = new Dictionary<string, string>();
            var currentQuestionIndex = 0;

            // إرسال السؤال الأول
            await SendQuestion(channel, user, questions[currentQuestionIndex], currentQuestionIndex + 1, questions.Length);

            // مراقبة الرسائل لمدة 5 دقائق
            var timeout = DateTime.Now.AddMinutes(5);
            var messageReceived = false;

            while (DateTime.Now < timeout && currentQuestionIndex < questions.Length)
            {
                // انتظار رسالة من المستخدم
                var messages = await channel.GetMessagesAsync(10).FlattenAsync();
                var userMessage = messages.FirstOrDefault(m => m.Author.Id == user.Id && m.Timestamp > DateTimeOffset.Now.AddSeconds(-30));

                if (userMessage != null && !messageReceived)
                {
                    messageReceived = true;
                    answers[questions[currentQuestionIndex]] = userMessage.Content;

                    // حذف رسالة المستخدم
                    await userMessage.DeleteAsync();

                    currentQuestionIndex++;

                    if (currentQuestionIndex < questions.Length)
                    {
                        // إرسال السؤال التالي
                        await SendQuestion(channel, user, questions[currentQuestionIndex], currentQuestionIndex + 1, questions.Length);
                        messageReceived = false;
                    }
                    else
                    {
                        // انتهت الأسئلة - إنشاء القصة
                        await GenerateAndSendStory(channel, user, answers);
                        break;
                    }
                }

                await Task.Delay(1000); // انتظار ثانية واحدة
            }

            if (currentQuestionIndex < questions.Length)
            {
                // انتهت المهلة
                await channel.SendMessageAsync(text: user.Mention, embed: new EmbedBuilder()
                    .WithColor(0xff0000)
                    .WithTitle("⏰ انتهت المهلة")
                    .WithDescription("انتهت مهلة الإجابة على الأسئلة.\nاستخدم `/join` مرة أخرى لبدء العملية من جديد.")
                    .Build());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Onboarding Questions Error] {ex.Message}");
            Console.WriteLine($"[Error] Onboarding Questions Error: {ex.Message}");
            
            await channel.SendMessageAsync(text: user.Mention, embed: new EmbedBuilder()
                .WithColor(0xff0000)
                .WithTitle("❌ خطأ")
                .WithDescription("حدث خطأ أثناء الأسئلة. حاول مرة أخرى.")
                .Build());
        }
    }

    private async Task SendQuestion(ITextChannel channel, SocketGuildUser user, string question, int questionNumber, int totalQuestions)
    {
        var embed = new EmbedBuilder()
            .WithColor(0x5865f2)
            .WithTitle($"❓ السؤال {questionNumber} من {totalQuestions}")
            .WithDescription(question)
            .WithFooter("اكتب إجابتك في هذه القناة")
            .Build();

        await channel.SendMessageAsync(text: user.Mention, embed: embed);
    }

    private async Task GenerateAndSendStory(ITextChannel channel, SocketGuildUser user, Dictionary<string, string> answers)
    {
        try
        {
            // إنشاء القصة
            var story = await GenerateStoryFromAnswers(answers, user.Username);

            // حفظ القصة
            await SaveStoryToFile(user.Id, story);

            // إرسال القصة في قناة القصص
            var familyStoriesChannelIdStr = Environment.GetEnvironmentVariable("FAMILY_STORIES_CHANNEL_ID");
            if (ulong.TryParse(familyStoriesChannelIdStr, out ulong familyStoriesChannelId) && familyStoriesChannelId != 0)
            {
                var familyStoriesChannel = Context.Client.GetChannel(familyStoriesChannelId) as IMessageChannel;
                if (familyStoriesChannel != null)
                {
                    var storyEmbed = new EmbedBuilder()
                        .WithColor(0xff6b35) // برتقالي للمستخدمين بدون دعوة
                        .WithTitle("🎭 قصة جديدة في العائلة")
                        .WithDescription(story)
                        .WithFooter($"قصة {user.Username}")
                        .WithTimestamp(DateTimeOffset.Now)
                        .Build();

                    await familyStoriesChannel.SendMessageAsync(text: user.Mention, embed: storyEmbed);
                }
            }

            // رسالة نجاح
            await channel.SendMessageAsync(text: user.Mention, embed: new EmbedBuilder()
                .WithColor(0x00ff00)
                .WithTitle("🎉 تم إنشاء قصتك بنجاح!")
                .WithDescription("تم إرسال قصتك إلى قناة القصص.\nتم ترقيتك إلى رول Associate.")
                .Build());

            // ترقية العضو
            await PromoteUserToAssociate(user);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Generate Story Error] {ex.Message}");
            Console.WriteLine($"[Error] Story Generation Error: {ex.Message}");
            
            await channel.SendMessageAsync(text: user.Mention, embed: new EmbedBuilder()
                .WithColor(0xff0000)
                .WithTitle("❌ خطأ")
                .WithDescription("حدث خطأ أثناء إنشاء القصة. حاول مرة أخرى.")
                .Build());
        }
    }

    private async Task<string> GenerateStoryFromAnswers(Dictionary<string, string> answers, string username)
    {
        try
        {
            var story = $"🎭 **قصة {username}**\n\n";
            story += $"**الاسم:** {answers["ما اسمك الحقيقي؟"]}\n";
            story += $"**العمر:** {answers["كم عمرك؟"]}\n";
            story += $"**الاهتمامات:** {answers["ما هي اهتماماتك؟"]}\n";
            story += $"**التخصص:** {answers["ما هو تخصصك؟"]}\n";
            story += $"**الميزة:** {answers["ما هي ميزتك؟"]}\n";
            story += $"**العيب:** {answers["ما هو عيبك؟"]}\n";
            story += $"**المكان المفضل:** {answers["ما هو المكان المفضل لديك؟"]}\n\n";
            story += "🌟 مرحباً بك في عائلة BitMob!";

            return story;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Generate Story From Answers Error] {ex.Message}");
            Console.WriteLine($"[Error] Story Generation from Answers Error: {ex.Message}");
            
            return $"🎭 **قصة {username}**\n\nحدث خطأ أثناء إنشاء القصة التفصيلية، لكن مرحباً بك في عائلة BitMob! 🌟";
        }
    }

    private async Task SaveStoryToFile(ulong userId, string story)
    {
        try
        {
            const string StoriesFile = "stories.json";
            var stories = new Dictionary<ulong, string>();

            if (File.Exists(StoriesFile))
            {
                var content = File.ReadAllText(StoriesFile);
                if (!string.IsNullOrEmpty(content))
                {
                    stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(content) ?? new Dictionary<ulong, string>();
                }
            }

            stories[userId] = story;
            File.WriteAllText(StoriesFile, JsonConvert.SerializeObject(stories, Formatting.Indented));
            
            Console.WriteLine($"[SaveStory] Story saved for user {userId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SaveStoryToFile Error] {ex.Message}");
            Console.WriteLine($"[Error] Story Save Error: {ex.Message}");
        }
    }




}

// Invite Information Class
public class InviteInfo
{
    public string InviterName { get; set; } = "";
    public ulong InviterId { get; set; }
    public string InviteCode { get; set; } = "";
    public DateTime JoinDate { get; set; }
}


