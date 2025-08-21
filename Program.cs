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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

class Program
{
    private DiscordSocketClient _client;
    private InteractionService _interactionService;
    private Dictionary<string, int> _inviteUses = new Dictionary<string, int>();
    string _token = "";
    string _ChatGPTApiKey = "";
    
    // Configuration - Load from environment variables for security
    private ulong storyChannelId;
    private ulong joinFamilyChannelId; // قناة Join the Family للأسئلة
    private ulong ownerId; // ID الـ Owner
    private ulong logChannelId;
    private ulong associateRoleId;
    private ulong outsiderRoleId;
    private const string StoriesFile = "stories.json";

    static async Task Main(string[] args)
    {
        var program = new Program();
        
        // بدء البوت في الخلفية
        _ = Task.Run(() => program.StartBotAsync());
        
        // بدء Web Application
        await program.StartWebAppAsync(args);
    }

    public async Task StartBotAsync()
    {
        try
        {
            Console.WriteLine("[Bot] Starting Discord bot...");
            await MainAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bot Error] {ex}");
        }
    }

    public async Task StartWebAppAsync(string[] args)
    {
        try
        {
            Console.WriteLine("[Web] Starting web application...");
            
            var builder = WebApplication.CreateBuilder(args);
            
            // إضافة الخدمات المطلوبة
            builder.Services.AddLogging();
            
            var app = builder.Build();
            
            // تكوين الـ middleware
            app.UseRouting();
            
            // إضافة endpoint بسيط للـ health check
            app.MapGet("/", async context =>
            {
                await context.Response.WriteAsync("Bot is running!");
            });
            
            // إضافة endpoint للـ health check
            app.MapGet("/health", async context =>
            {
                string botStatus;
                if (_client == null)
                {
                    botStatus = "Not Initialized";
                }
                else if (_client.ConnectionState == ConnectionState.Connected)
                {
                    botStatus = "Connected";
                }
                else
                {
                    botStatus = $"Disconnected ({_client.ConnectionState})";
                }
                await context.Response.WriteAsync($"Bot Status: {botStatus}");
            });
            
            // بدء التطبيق على أي port متاح
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            Console.WriteLine($"[Web] Starting web app on port {port}");
            
            await app.RunAsync($"http://0.0.0.0:{port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Web Error] {ex}");
        }
    }

    public async Task MainAsync()
    {
        Env.Load();

        _token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        _ChatGPTApiKey = Environment.GetEnvironmentVariable("OPENAI_KEY");
        
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
        _client.UserJoined += async (user) =>
        {
            _ = Task.Run(() => HandleUserJoinedAsync(user));
        };

        // Add interaction handler
        _client.InteractionCreated += HandleInteractionAsync;

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();

        // Register slash commands
        await RegisterCommandsAsync();

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
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, null);
            
            if (!result.IsSuccess)
            {
                Console.WriteLine($"[Interaction Error] {result.ErrorReason}");
                await LogError("Interaction Error", result.ErrorReason, $"Failed to execute command: {interaction.Data.Name}");
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
        Console.WriteLine($"[Ready] {_client.CurrentUser} is connected!");

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
            Console.WriteLine($"[UserJoined] {user.Username} joined.");

            var guild = user.Guild;
            var invitesAfter = await guild.GetInvitesAsync();

            RestInviteMetadata usedInvite = null;
            foreach (var invite in invitesAfter)
            {
                var previousUses = _inviteUses.ContainsKey(invite.Code) ? _inviteUses[invite.Code] : 0;
                if ((invite.Uses ?? 0) > previousUses)
                {
                    usedInvite = invite;
                    break;
                }
            }

            foreach (var invite in invitesAfter)
                _inviteUses[invite.Code] = invite.Uses ?? 0;

            // Check if user joined without invite
            bool hasInviter = usedInvite?.Inviter != null;
            
            string inviterName = hasInviter ? usedInvite.Inviter.Username : "غير معروف";
            ulong inviterId = hasInviter ? usedInvite.Inviter.Id : 0;

            var inviterUser = hasInviter ? guild.GetUser(inviterId) : null;
            var inviterRole = hasInviter && inviterUser != null ? inviterUser.Roles
                .Where(r => r.Id != guild.EveryoneRole.Id)
                .OrderByDescending(r => r.Position)
                .FirstOrDefault()?.Name ?? "بدون رول" : "بدون رول";

            string inviterStory = hasInviter ? LoadStory(inviterId) : "";

            // الحصول على قناة Join the Family
            var joinChannel = _client.GetChannel(joinFamilyChannelId) as ITextChannel;
            if (joinChannel == null)
            {
                Console.WriteLine("[Warning] Join Family channel not found, onboarding cancelled");
                await LogError("Join Channel Error", "Join Family channel not found", $"User {user.Username} could not be onboarded");
                return;
            }

            // بدء عملية الأسئلة في القناة
            await SendWelcomeToChannel(joinChannel, user, hasInviter);
            
            // انتظار قليل ليقرأ التعريف
            await Task.Delay(3000);

            string name = await AskQuestionInChannel(joinChannel, user, "اسمك الحقيقي ايه؟");
            if (name == "لم يتم الرد في الوقت المحدد") return;

            string age = await AskQuestionInChannel(joinChannel, user, "سنك كام؟");
            if (age == "لم يتم الرد في الوقت المحدد") return;

            string interest = await AskQuestionInChannel(joinChannel, user, "داخل السرفر ليه؟");
            if (interest == "لم يتم الرد في الوقت المحدد") return;

            string specialty = await AskQuestionInChannel(joinChannel, user, "تخصصك أو شغفك؟");
            if (specialty == "لم يتم الرد في الوقت المحدد") return;

            string strength = await AskQuestionInChannel(joinChannel, user, "أهم ميزة عندك؟");
            if (strength == "لم يتم الرد في الوقت المحدد") return;

            string weakness = await AskQuestionInChannel(joinChannel, user, "أكبر عيب عندك؟");
            if (weakness == "لم يتم الرد في الوقت المحدد") return;

            string favoritePlace = await AskQuestionInChannel(joinChannel, user, "مكان بتحبه تروح له؟");
            if (favoritePlace == "لم يتم الرد في الوقت المحدد") return;

            // Check if user answered all questions
            bool answeredAllQuestions = !string.IsNullOrWhiteSpace(name) && 
                                      !string.IsNullOrWhiteSpace(age) && 
                                      !string.IsNullOrWhiteSpace(interest) && 
                                      !string.IsNullOrWhiteSpace(specialty) && 
                                      !string.IsNullOrWhiteSpace(strength) && 
                                      !string.IsNullOrWhiteSpace(weakness) && 
                                      !string.IsNullOrWhiteSpace(favoritePlace) &&
                                      name != "لم يتم الرد في الوقت المحدد" &&
                                      age != "لم يتم الرد في الوقت المحدد" &&
                                      interest != "لم يتم الرد في الوقت المحدد" &&
                                      specialty != "لم يتم الرد في الوقت المحدد" &&
                                      strength != "لم يتم الرد في الوقت المحدد" &&
                                      weakness != "لم يتم الرد في الوقت المحدد" &&
                                      favoritePlace != "لم يتم الرد في الوقت المحدد";

            // Assign role based on answering questions
            if (answeredAllQuestions)
            {
                if (associateRoleId != 0)
                {
                    await AssignRole(user, associateRoleId, "Associate");
                    await SendMessageToJoinChannel(joinChannel, user, "🎉 مبروك! اخدت رول **Associate** عشان جاوبت على كل الأسئلة!");
                }
                else
                {
                    await SendMessageToJoinChannel(joinChannel, user, "🎉 مبروك! جاوبت على كل الأسئلة! (رول Associate غير مُعد في التكوين)");
                    Console.WriteLine("[Warning] Associate role assignment skipped - role ID not configured");
                }
            }
            else
            {
                if (outsiderRoleId != 0)
                {
                    await AssignRole(user, outsiderRoleId, "Outsider");
                    await SendMessageToJoinChannel(joinChannel, user, "⚠️ اخدت رول **Outsider** عشان ما جاوبتش على كل الأسئلة.");
                }
                else
                {
                    await SendMessageToJoinChannel(joinChannel, user, "⚠️ ما جاوبتش على كل الأسئلة. (رول Outsider غير مُعد في التكوين)");
                    Console.WriteLine("[Warning] Outsider role assignment skipped - role ID not configured");
                }
            }

            Console.WriteLine("[Info] Generating story...");

            string story = await GenerateStory(name, age, interest, specialty, strength, weakness, favoritePlace, inviterName, inviterRole, inviterStory, hasInviter);

            SaveStory(user.Id, story);
            Console.WriteLine("[Info] Story saved successfully.");

            if (storyChannelId != 0)
            {
                var storyChannel = _client.GetChannel(storyChannelId) as IMessageChannel;
                if (storyChannel != null)
                {
                    if (hasInviter)
                    {
                        await SendMessageSafe(storyChannel, $"🤝 {user.Mention} مرحباً بك في العائلة!\n{story}");
                    }
                    else
                    {
                        await SendMessageSafe(storyChannel, $"👤 {user.Mention} شخص مجهول انضم!\n{story}");
                    }
                    Console.WriteLine("[Info] Story posted to channel successfully.");
                }
                else
                {
                    Console.WriteLine($"[Warning] Story channel with ID {storyChannelId} not found in this server.");
                }
            }
            else
            {
                Console.WriteLine("[Info] Story channel not configured - skipping channel posting.");
            }

            await SendMessageToJoinChannel(joinChannel, user, story);
            Console.WriteLine("[Info] Story sent to join channel and story channel successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Error] Exception in HandleUserJoinedAsync: " + ex);
            await LogError("User Join Processing Error", ex.ToString(), $"Failed to process user join for {user.Username}");
            
            // Try to send error message to user
            try
            {
                var dm = await user.CreateDMChannelAsync();
                await SendDM(dm, "عذراً، حدث خطأ أثناء معالجة انضمامك. الرجاء التواصل مع الإدارة.");
            }
            catch (Exception dmEx)
            {
                Console.WriteLine($"[Error] Failed to send error DM: {dmEx.Message}");
                await LogError("Error DM Failed", dmEx.Message, $"Failed to send error DM to {user.Username}");
            }
        }
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

            var role = user.Guild.GetRole(roleId);
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

    private async Task SendDM(IDMChannel dm, string message)
    {
        try
        {
            await dm.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DM Error] " + ex.Message);
            await LogError("DM Error", ex.Message, "Failed to send DM message");
        }
    }

    private async Task SendWelcomeToChannel(ITextChannel channel, SocketGuildUser user, bool hasInviter)
    {
        try
        {
            var introMessage = $@"{user.Mention} 🎭 **أهلاً وسهلاً {user.Username}!**

أنا **BitMob Bot** 🤖، مرحباً بك في عالم **The Underworld** - عالم المافيا والبرمجة!

🎆 ** أنا مين ؟**
• المساعد الشخصي في السيرفر
• منشئ القصص الملحمية باستخدام الذكاء الاصطناعي
• حارس أسرار العائلة وكاتب تاريخها

🎯 **بعمل ايه هنا؟**
• هسالك شوية اساله بسيطه عشان اتعرف عليك واعملك قصة تناسبك عشان الناس تعرفك واديلك role في العائلة

⚠️ **مهم جداً:**
• عندك **5 دقايق** للرد على كل سؤال لو ما ردتش في الوقت، هوقف العملية
• مش لازم الاجابه تكون نموذجية او حقيقية 100% خليك كيرييتف

{(hasInviter ? "🤝 **أنت مدعو من عضو في العائلة** - دي نقطة في صالحك!" : "👤 **انت دخلت بدون دعوة** - لكن ممكن تثبت نفسك!")}

استعد للانضمام لعالم **The Underworld**! 🌃

---
**هنبدأ بالأسئلة دلوقتي...**";

            await SendMessageToJoinChannel(channel, user, introMessage);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Bot Introduction Error] " + ex.Message);
            await LogError("Bot Introduction Error", ex.Message, "Failed to send bot introduction to join channel");
        }
    }

    private async Task SendBotIntroduction(IDMChannel dm, string username, bool hasInviter)
    {
        try
        {
            var introMessage = $@"🎭 **أهلاً وسهلاً {username}!**

أنا **BitMob Bot** 🤖، مرحباً بك في عالم **The Underworld** - عالم المافيا والبرمجة!

🌟 ** أنا مين ؟**
• المساعد الشخصي في السيرفر
• منشئ القصص الملحمية باستخدام الذكاء الاصطناعي
• حارس أسرار العائلة وكاتب تاريخها

🎯 **بعمل ايه هنا؟**
• هسالك شوية اساله بسيطه عشان اتعرف عليك واعملك قصة تناسبك عشان الناس تعرفك واديلك role في العائلة

⚠️ **مهم جداً:**
• عندك دقيقة واحدة للرد على كل سؤال لو ما ردتش في الوقت، هوقف العملية
• مش لازم الاجابه تكون نموذجية او حقيقية 100% خليك كيرييتف 

{(hasInviter ? "🤝 **أنت مدعو من عضو في العائلة** - دي نقطة في صالحك!" : "👤 **انت دخلت بدون دعوة** - لكن ممكن تثبت نفسك!")}

استعد للانضمام لعالم **The Underworld**! 🌃

---
**هنبدأ بالأسئلة دلوقتي...**";

            await dm.SendMessageAsync(introMessage);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Bot Introduction Error] " + ex.Message);
            await LogError("Bot Introduction Error", ex.Message, "Failed to send bot introduction");
        }
    }

    private async Task LogError(string errorType, string errorMessage, string context = "")
    {
        try
        {
            if (logChannelId == 0 || _client == null)
                return;

            var logChannel = _client.GetChannel(logChannelId) as IMessageChannel;
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

    private async Task<string> AskQuestionInChannel(ITextChannel channel, SocketGuildUser user, string question)
    {
        await SendMessageToJoinChannel(channel, user, $"💬 **سؤال:** {question}");
        var response = await WaitForUserResponseInChannel(channel, user);
        
        // إذا لم يرد المستخدم، أرسل رسالة إيقاف
        if (response == "لم يتم الرد في الوقت المحدد")
        {
            await SendMessageToJoinChannel(channel, user, "⏰ انتهى الوقت المحدد للرد (5 دقايق). سيتم إيقاف عملية التسجيل.");
        }
        
        return response;
    }

    private async Task<string> AskQuestion(IDMChannel dm, string question)
    {
        await SendDM(dm, question);
        var response = await WaitForUserResponse(dm);
        
        // إذا لم يرد المستخدم، أرسل رسالة إيقاف
        if (response == "لم يتم الرد في الوقت المحدد")
        {
            await SendDM(dm, "⏰ انتهى الوقت المحدد للرد. سيتم إيقاف عملية التسجيل.");
        }
        
        return response;
    }

    private async Task<string> WaitForUserResponseInChannel(ITextChannel channel, SocketGuildUser user, int timeoutSeconds = 300) // 5 دقايق
    {
        var tcs = new TaskCompletionSource<string>();

        Task Handler(SocketMessage msg)
        {
            if (msg.Channel.Id == channel.Id && msg.Author.Id == user.Id && !msg.Author.IsBot)
                tcs.TrySetResult(msg.Content);
            return Task.CompletedTask;
        }

        _client.MessageReceived += Handler;

        var resultTask = tcs.Task;
        if (await Task.WhenAny(resultTask, Task.Delay(timeoutSeconds * 1000)) == resultTask)
        {
            _client.MessageReceived -= Handler;
            return resultTask.Result;
        }
        else
        {
            _client.MessageReceived -= Handler;
            Console.WriteLine($"[Timeout] User {user.Username} did not respond in time (5 minutes).");
            return "لم يتم الرد في الوقت المحدد";
        }
    }

    private async Task<string> WaitForUserResponse(IDMChannel dm, int timeoutSeconds = 60)
    {
        var tcs = new TaskCompletionSource<string>();

        Task Handler(SocketMessage msg)
        {
            if (msg.Channel.Id == dm.Id && !msg.Author.IsBot)
                tcs.TrySetResult(msg.Content);
            return Task.CompletedTask;
        }

        _client.MessageReceived += Handler;

        var resultTask = tcs.Task;
        if (await Task.WhenAny(resultTask, Task.Delay(timeoutSeconds * 1000)) == resultTask)
        {
            _client.MessageReceived -= Handler;
            return resultTask.Result;
        }
        else
        {
            _client.MessageReceived -= Handler;
            Console.WriteLine("[Timeout] User did not respond in time.");
            return "لم يتم الرد في الوقت المحدد";
        }
    }

    private async Task SendMessageToJoinChannel(ITextChannel channel, SocketGuildUser user, string message)
    {
        try
        {
            // إرسال الرسالة مع تحديد الأذونات (فقط Owner والشخص المذكور)
            var allowedUsers = new List<ulong> { ownerId, user.Id };
            
            var embed = new EmbedBuilder()
                .WithColor(0x2f3136) // لون رمادي داكن
                .WithDescription(message)
                .WithFooter($"🔒 هذه الرسالة مرئية فقط للـ Owner و{user.Username}")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await channel.SendMessageAsync(embed: embed);
            Console.WriteLine($"[Info] Message sent to join channel for {user.Username}: {message.Substring(0, Math.Min(50, message.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to send message to join channel: {ex.Message}");
            await LogError("Join Channel Message Error", ex.ToString(), $"Failed to send message for {user.Username}");
        }
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

            stories[userId] = story;
            File.WriteAllText(StoriesFile, JsonConvert.SerializeObject(stories, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SaveStory Error] " + ex);
            await LogError("Story Save Error", ex.ToString(), "Failed to save user story to file");
        }
    }

    private string LoadStory(ulong userId)
    {
        try
        {
            if (!File.Exists(StoriesFile)) return "";
            var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile));
            return stories.ContainsKey(userId) ? stories[userId] : "";
        }
        catch (Exception ex)
        {
            Console.WriteLine("[LoadStory Error] " + ex);
            _ = Task.Run(() => LogError("Story Load Error", ex.ToString(), "Failed to load user story from file"));
            return "";
        }
    }

    private void LoadConfiguration()
    {
        try
        {
            // Load channel and role IDs from environment variables
            var storyChannelIdStr = Environment.GetEnvironmentVariable("STORY_CHANNEL_ID");
            var joinFamilyChannelIdStr = Environment.GetEnvironmentVariable("JOIN_FAMILY_CHANNEL_ID");
            var ownerIdStr = Environment.GetEnvironmentVariable("OWNER_ID");
            var logChannelIdStr = Environment.GetEnvironmentVariable("LOG_CHANNEL_ID");
            var associateRoleIdStr = Environment.GetEnvironmentVariable("ASSOCIATE_ROLE_ID");
            var outsiderRoleIdStr = Environment.GetEnvironmentVariable("OUTSIDER_ROLE_ID");

            if (ulong.TryParse(storyChannelIdStr, out ulong storyId))
            {
                storyChannelId = storyId;
                Console.WriteLine($"[Config] Story channel ID loaded: {storyChannelId}");
            }
            else
            {
                Console.WriteLine("[Config Warning] STORY_CHANNEL_ID not found or invalid. Stories won't be posted to channels.");
                storyChannelId = 0;
            }

            if (ulong.TryParse(joinFamilyChannelIdStr, out ulong joinFamilyId))
            {
                joinFamilyChannelId = joinFamilyId;
                Console.WriteLine($"[Config] Join Family channel ID loaded: {joinFamilyChannelId}");
            }
            else
            {
                Console.WriteLine("[Config Error] JOIN_FAMILY_CHANNEL_ID not found or invalid. Onboarding won't work without this!");
                joinFamilyChannelId = 0;
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

            var story = LoadUserStory(user.Id);
            
            if (string.IsNullOrEmpty(story))
            {
                await FollowupAsync($"❌ لا توجد قصة محفوظة لـ {user.Mention}");
                return;
            }

            // Split story if it's too long
            if (story.Length > 2000)
            {
                var chunks = SplitMessage(story, 2000);
                await FollowupAsync($"📖 **قصة {user.Username}**\n{chunks[0]}");
                
                for (int i = 1; i < chunks.Length; i++)
                {
                    await Context.Channel.SendMessageAsync(chunks[i]);
                }
            }
            else
            {
                await FollowupAsync($"📖 **قصة {user.Username}**\n{story}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Story Command Error] {ex}");
            await FollowupAsync("❌ حدث خطأ أثناء عرض القصة");
        }
    }

    private string LoadUserStory(ulong userId)
    {
        try
        {
            const string StoriesFile = "stories.json";
            if (!File.Exists(StoriesFile)) return "";
            
            var stories = JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(StoriesFile));
            return stories.ContainsKey(userId) ? stories[userId] : "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Load Story Error] {ex}");
            return "";
        }
    }

    private string[] SplitMessage(string message, int maxLength)
    {
        var chunks = new List<string>();
        for (int i = 0; i < message.Length; i += maxLength)
        {
            chunks.Add(message.Substring(i, Math.Min(maxLength, message.Length - i)));
        }
        return chunks.ToArray();
    }
}
