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

class Program
{
    private DiscordSocketClient? _client;
    private InteractionService? _interactionService;
    private Dictionary<string, int> _inviteUses = new Dictionary<string, int>();
    private HashSet<ulong> _processedUsers = new HashSet<ulong>();
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
            await MainAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bot Error] {ex}");
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

        if (_client != null)
        {
            foreach (var guild in _client.Guilds)
            {
                var invites = await guild.GetInvitesAsync();
                foreach (var invite in invites)
                {
                    _inviteUses[invite.Code] = invite.Uses ?? 0;
                }
            }
        }
    }

    private async Task HandleUserJoinedAsync(SocketGuildUser user)
    {
        try
        {
            // منع تكرار معالجة نفس المستخدم
            if (_processedUsers.Contains(user.Id))
            {
                Console.WriteLine($"[Warning] User {user.Username} already being processed, skipping...");
                return;
            }
            
            _processedUsers.Add(user.Id);
            Console.WriteLine($"[UserJoined] {user.Username} joined.");

            var guild = user.Guild;
            
            // إضافة تأخير صغير للتأكد من تحديث الانفايت
            await Task.Delay(1000);
            
            var invitesAfter = await guild.GetInvitesAsync();

            RestInviteMetadata usedInvite = null;
            Console.WriteLine($"[Invite Detection] Checking invites for user {user.Username}...");
            
            // البحث عن الانفايت المستخدم
            foreach (var invite in invitesAfter)
            {
                var previousUses = _inviteUses.ContainsKey(invite.Code) ? _inviteUses[invite.Code] : 0;
                var currentUses = invite.Uses ?? 0;
                
                Console.WriteLine($"[Invite Detection] Invite {invite.Code} by {invite.Inviter?.Username ?? "Unknown"}: Previous={previousUses}, Current={currentUses}");
                
                if (currentUses > previousUses)
                {
                    usedInvite = invite;
                    Console.WriteLine($"[Invite Detection] Found used invite: {invite.Code} by {invite.Inviter?.Username ?? "Unknown"}");
                    break;
                }
            }

            // إذا لم نجد انفايت، نحاول البحث بطريقة أخرى
            if (usedInvite == null)
            {
                Console.WriteLine($"[Invite Detection] Trying alternative detection method...");
                
                // البحث عن الانفايت الذي زاد استخدامه
                var mostUsedInvite = invitesAfter
                    .Where(i => i.Uses > 0)
                    .OrderByDescending(i => i.Uses)
                    .FirstOrDefault();
                
                if (mostUsedInvite != null)
                {
                    usedInvite = mostUsedInvite;
                    Console.WriteLine($"[Invite Detection] Using most used invite: {mostUsedInvite.Code} by {mostUsedInvite.Inviter?.Username ?? "Unknown"}");
                }
            }

            // Update invite uses tracking
            foreach (var invite in invitesAfter)
            {
                _inviteUses[invite.Code] = invite.Uses ?? 0;
            }
            
            if (usedInvite == null)
            {
                Console.WriteLine($"[Invite Detection] No invite found for user {user.Username} - they may have joined without an invite");
            }

            // Check if user joined without invite
            bool hasInviter = usedInvite?.Inviter != null;
            
            ulong inviterId = hasInviter ? usedInvite.Inviter.Id : 0;
            var inviterUser = hasInviter ? guild.GetUser(inviterId) : null;
            
            // استخدام Nickname إذا كان موجود، وإلا Username
            string inviterName = "غير معروف";
            if (hasInviter && inviterUser != null)
            {
                inviterName = !string.IsNullOrEmpty(inviterUser.Nickname) ? inviterUser.Nickname : inviterUser.Username;
            }
            
            var inviterRole = hasInviter && inviterUser != null ? inviterUser.Roles
                .Where(r => r.Id != guild.EveryoneRole.Id)
                .OrderByDescending(r => r.Position)
                .FirstOrDefault()?.Name ?? "بدون رول" : "بدون رول";

            string inviterStory = hasInviter ? LoadStory(inviterId) : "";

            // Log invite information for debugging
            Console.WriteLine($"[Invite Info] User {user.Username} joined via invite:");
            Console.WriteLine($"[Invite Info] - Has Inviter: {hasInviter}");
            Console.WriteLine($"[Invite Info] - Inviter Name: {inviterName}");
            Console.WriteLine($"[Invite Info] - Inviter ID: {inviterId}");
            Console.WriteLine($"[Invite Info] - Inviter Role: {inviterRole}");
            Console.WriteLine($"[Invite Info] - Used Invite Code: {usedInvite?.Code ?? "Unknown"}");

            // Save invite information to history
            SaveInviteHistory(user.Id, inviterName, inviterId, usedInvite?.Code ?? "Unknown", DateTime.Now);

            // التحقق من وجود قصة مسبقة للمستخدم
            bool userHasStory = await CheckUserInStoryChannel(user.Id);
            if (userHasStory)
            {
                Console.WriteLine($"[Info] User {user.Username} already has a story, skipping onboarding");
                


                // إرسال رسالة ترحيب في قناة القصص للعضو القديم
                var storyChannel = _client?.GetChannel(storyChannelId) as IMessageChannel;
                if (storyChannel != null)
                {
                    var welcomeBackEmbed = new EmbedBuilder()
                        .WithColor(new Color(0x00ff00)) // لون أخضر
                        .WithAuthor("🎭 مرحباً بعودتك!", iconUrl: user.GetAvatarUrl())
                        .WithTitle($"مرحباً بعودتك {user.Username}!")
                        .WithDescription("أنت عضو قديم في العائلة ولديك قصة مسجلة بالفعل! 📖\n" +
                                       "لا تحتاج لإعادة عملية التسجيل.\n\n" +
                                       "أهلاً بعودتك لعالم **The Underworld**! 🌃")
                        .WithFooter($"تاريخ العودة: {DateTime.Now:dd/MM/yyyy HH:mm}")
                        .WithTimestamp(DateTimeOffset.Now)
                        .Build();

                    await storyChannel.SendMessageAsync(text: user.Mention, embed: welcomeBackEmbed);
                }
                
                // إعطاء المستخدم رول Associate إذا كان متوفر (لأنه عضو قديم)
                if (associateRoleId != 0)
                {
                    await AssignRole(user, associateRoleId, "Associate");
                    Console.WriteLine($"[Info] Assigned Associate role to returning member {user.Username}");
                }
                else
                {
                    Console.WriteLine($"[Warning] Could not assign Associate role to returning member {user.Username} - role not configured");
                }
                
                return; // إنهاء العملية هنا
            }

            // الحصول على قناة Join the Family للأعضاء الجدد
            var newMemberJoinChannel = _client?.GetChannel(joinFamilyChannelId) as ITextChannel;
            if (newMemberJoinChannel == null)
            {
                Console.WriteLine("[Warning] Join Family channel not found, onboarding cancelled");
                await LogError("Join Channel Error", "Join Family channel not found", $"User {user.Username} could not be onboarded");
                return;
            }

            // بدء عملية الأسئلة في القناة للأعضاء الجدد فقط
            Console.WriteLine($"[Info] Starting onboarding process for new member {user.Username}");
            
            // إعطاء المستخدم صلاحية الكتابة في القناة
            await GiveUserWritePermission(newMemberJoinChannel, user);
            
            await SendWelcomeToChannel(newMemberJoinChannel, user, hasInviter, inviterName, inviterRole);
            
            // انتظار قليل ليقرأ التعريف
            await Task.Delay(3000);

            string name = await AskQuestionInChannel(newMemberJoinChannel, user, "اسمك الحقيقي ايه؟");
            if (name == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

            string age = await AskQuestionInChannel(newMemberJoinChannel, user, "سنك كام؟");
            if (age == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

            string interest = await AskQuestionInChannel(newMemberJoinChannel, user, "داخل السرفر ليه؟");
            if (interest == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

            string specialty = await AskQuestionInChannel(newMemberJoinChannel, user, "تخصصك أو شغفك؟");
            if (specialty == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

            string strength = await AskQuestionInChannel(newMemberJoinChannel, user, "أهم ميزة عندك؟");
            if (strength == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

            string weakness = await AskQuestionInChannel(newMemberJoinChannel, user, "أكبر عيب عندك؟");
            if (weakness == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

            string favoritePlace = await AskQuestionInChannel(newMemberJoinChannel, user, "مكان بتحبه تروح له؟");
            if (favoritePlace == "لم يتم الرد في الوقت المحدد") 
            {
                await RemoveUserWritePermission(newMemberJoinChannel, user);
                return;
            }

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
                    await SendMessageToJoinChannel(newMemberJoinChannel, user, "🎉 مبروك! اخدت رول **Associate** عشان جاوبت على كل الأسئلة!");
                }
                else
                {
                    await SendMessageToJoinChannel(newMemberJoinChannel, user, "🎉 مبروك! جاوبت على كل الأسئلة! (رول Associate غير مُعد في التكوين)");
                    Console.WriteLine("[Warning] Associate role assignment skipped - role ID not configured");
                }
            }
            else
            {
                if (outsiderRoleId != 0)
                {
                    await AssignRole(user, outsiderRoleId, "Outsider");
                    await SendMessageToJoinChannel(newMemberJoinChannel, user, "⚠️ اخدت رول **Outsider** عشان ما جاوبتش على كل الأسئلة.");
                }
                else
                {
                    await SendMessageToJoinChannel(newMemberJoinChannel, user, "⚠️ ما جاوبتش على كل الأسئلة. (رول Outsider غير مُعد في التكوين)");
                    Console.WriteLine("[Warning] Outsider role assignment skipped - role ID not configured");
                }
            }

            Console.WriteLine("[Info] Generating story...");

            string story = await GenerateStory(name, age, interest, specialty, strength, weakness, favoritePlace, inviterName, inviterRole, inviterStory, hasInviter);

            // حفظ القصة في الملف للاستخدام المستقبلي
            SaveStory(user.Id, story);
            Console.WriteLine("[Info] Story generated and saved successfully.");

            if (storyChannelId != 0)
            {
                var storyChannel = _client?.GetChannel(storyChannelId) as IMessageChannel;
                if (storyChannel != null)
                {
                    // تحديد لون الـ Embed بناءً على وجود الدعوة
                    Color embedColor;
                    if (hasInviter)
                    {
                        embedColor = new Color(0x00ff00); // أخضر 🟩 للمدعوين
                        Console.WriteLine($"[Info] User {user.Username} joined via invite - using green color");
                    }
                    else
                    {
                        embedColor = new Color(0xff6b35); // برتقالي 🟧 للمجهولين
                        Console.WriteLine($"[Info] User {user.Username} joined without invite - using orange color");
                    }

                    // إنشاء Embed منسق للقصة في قناة القصص
                    var storyEmbed = new EmbedBuilder()
                        .WithColor(embedColor)
                        .WithAuthor("📜🎭 قصة العضو", iconUrl: user.GetAvatarUrl())
                        .WithTitle(ExtractStoryTitle(story))
                        .WithDescription(story)
                        .WithFooter($"تم إنشاء القصة في {DateTime.Now:dd/MM/yyyy HH:mm}")
                        .WithTimestamp(DateTimeOffset.Now)
                        .Build();

                    await storyChannel.SendMessageAsync(embed: storyEmbed);
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

            // إرسال رابط القصة مرة واحدة فقط
            await SendStoryCompletionMessage(newMemberJoinChannel, user, story);
            
            // إزالة صلاحية الكتابة من المستخدم
            await RemoveUserWritePermission(newMemberJoinChannel, user);
            
            // حذف رسائل الأسئلة بعد الانتهاء
            await CleanupQuestionMessages(newMemberJoinChannel, user);
            
            Console.WriteLine("[Info] Story sent to join channel and story channel successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Error] Exception in HandleUserJoinedAsync: " + ex);
            await LogError("User Join Processing Error", ex.ToString(), $"Failed to process user join for {user.Username}");
            

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



    private async Task SendWelcomeToChannel(ITextChannel channel, SocketGuildUser user, bool hasInviter, string inviterName = "", string inviterRole = "")
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

{(hasInviter ? $"🤝 **أنت مدعو من {inviterName} ({inviterRole})** - دي نقطة في صالحك!" : "👤 **انت دخلت بدون دعوة** - لكن ممكن تثبت نفسك!")}

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



    private async Task<string> WaitForUserResponseInChannel(ITextChannel channel, SocketGuildUser user, int timeoutSeconds = 300) // 5 دقايق
    {
        var tcs = new TaskCompletionSource<string>();

        Task Handler(SocketMessage msg)
        {
            if (msg.Channel.Id == channel.Id && msg.Author.Id == user.Id && !msg.Author.IsBot)
                tcs.TrySetResult(msg.Content);
            return Task.CompletedTask;
        }

        if (_client != null)
        {
            _client.MessageReceived += Handler;
        }

        var resultTask = tcs.Task;
        if (await Task.WhenAny(resultTask, Task.Delay(timeoutSeconds * 1000)) == resultTask)
        {
            if (_client != null)
            {
                _client.MessageReceived -= Handler;
            }
            return resultTask.Result;
        }
        else
        {
            if (_client != null)
            {
                _client.MessageReceived -= Handler;
            }
            Console.WriteLine($"[Timeout] User {user.Username} did not respond in time (5 minutes).");
            return "لم يتم الرد في الوقت المحدد";
        }
    }



    private async Task GiveUserWritePermission(ITextChannel channel, SocketGuildUser user)
    {
        try
        {
            var permissionOverwrite = new OverwritePermissions(
                sendMessages: PermValue.Allow,
                viewChannel: PermValue.Allow
            );
            
            await channel.AddPermissionOverwriteAsync(user, permissionOverwrite);
            Console.WriteLine($"[Info] Gave write permission to {user.Username} in join channel");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to give write permission to {user.Username}: {ex.Message}");
            await LogError("Permission Error", ex.Message, $"Failed to give write permission to {user.Username}");
        }
    }

    private async Task RemoveUserWritePermission(ITextChannel channel, SocketGuildUser user)
    {
        try
        {
            await channel.RemovePermissionOverwriteAsync(user);
            Console.WriteLine($"[Info] Removed write permission from {user.Username} in join channel");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to remove write permission from {user.Username}: {ex.Message}");
            await LogError("Permission Error", ex.Message, $"Failed to remove write permission from {user.Username}");
        }
    }

    private async Task CleanupQuestionMessages(ITextChannel channel, SocketGuildUser user)
    {
        try
        {
            // حذف رسائل الأسئلة والردود (آخر 50 رسالة للتأكد من حذف كل شيء)
            var messages = await channel.GetMessagesAsync(50).FlattenAsync();
            var messagesToDelete = messages.Where(m => 
                (m.Author.Id == _client?.CurrentUser?.Id && (
                    m.Content.Contains("💬 **سؤال:**") ||
                    m.Content.Contains("🎭 **أهلاً وسهلاً") ||
                    m.Content.Contains("🎉 مبروك!") ||
                    m.Content.Contains("⚠️ اخدت رول") ||
                    m.Content.Contains("⏰ انتهى الوقت")
                )) ||
                (m.Author.Id == user.Id && !m.Author.IsBot)
            ).ToList();

            if (messagesToDelete.Any())
            {
                await channel.DeleteMessagesAsync(messagesToDelete);
                Console.WriteLine($"[Info] Deleted {messagesToDelete.Count} question/answer messages for {user.Username}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Could not cleanup messages for {user.Username}: {ex.Message}");
        }
    }

    private async Task SendStoryCompletionMessage(ITextChannel channel, SocketGuildUser user, string story)
    {
        try
        {
            string storyLink = $"https://discord.com/channels/{user.Guild?.Id}/{storyChannelId}";
            
            // إنشاء Embed للرسالة التوجيهية فقط (بدون القصة)
            var infoEmbed = new EmbedBuilder()
                .WithColor(new Color(0x00ff00))
                .WithTitle("🎉 مبروك! تم إنشاء قصتك بنجاح!")
                .WithDescription($"**مرحباً {user.Username}!**\n\n" +
                               $"تم إنشاء قصتك بنجاح وتم حفظها في قاعدة بيانات العائلة! 📖\n\n" +
                               $"**اقرأ قصتك هنا:**\n" +
                               $"🔗 {storyLink}\n\n" +
                               $"**أو اذهب إلى تشانل القصص مباشرة**")
                .WithFooter($"تم إنشاء القصة في {DateTime.Now:dd/MM/yyyy HH:mm}")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            // الحصول على قائمة المستخدمين المسموح لهم برؤية الرسالة
            var allowedUsers = GetAllowedUsersForPrivateMessage(user);
            
            // إرسال الرسالة التوجيهية فقط
            await channel.SendMessageAsync(text: user.Mention, embed: infoEmbed, allowedMentions: new AllowedMentions { UserIds = allowedUsers });
            
            Console.WriteLine($"[Info] Story completion message sent to {user.Username}");
            
            // انتظار قليل قبل حذف الرسائل
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to send story completion message: {ex.Message}");
            await LogError("Story Completion Error", ex.Message, $"Failed to send story completion message to {user.Username}");
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

    private List<ulong> GetAllowedUsersForPrivateMessage(SocketGuildUser user)
    {
        var allowedUsers = new List<ulong> { user.Id };
        
        // إضافة Owner إذا كان مُعد
        if (ownerId != 0)
        {
            allowedUsers.Add(ownerId);
        }
        
        // إضافة الأدمن (أي شخص له صلاحية إدارة السيرفر)
        var adminUsers = user.Guild.Users.Where(u => 
            u.GuildPermissions.Administrator || 
            u.GuildPermissions.ManageGuild ||
            u.GuildPermissions.ManageChannels
        ).Select(u => u.Id).ToList();
        
        allowedUsers.AddRange(adminUsers);
        return allowedUsers.Distinct().ToList(); // إزالة التكرار
    }

    private async Task SendMessageToJoinChannel(ITextChannel channel, SocketGuildUser user, string message)
    {
        try
        {
            // الحصول على قائمة المستخدمين المسموح لهم برؤية الرسالة
            var allowedUsers = GetAllowedUsersForPrivateMessage(user);
            
            var embed = new EmbedBuilder()
                .WithColor(0x2f3136) // لون رمادي داكن
                .WithDescription(message)
                .WithFooter($"🔒 هذه الرسالة مرئية فقط لـ {user.Username} والأدمن")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            // إرسال الرسالة مع تحديد الأذونات
            await channel.SendMessageAsync(
                text: user.Mention, 
                embed: embed, 
                allowedMentions: new AllowedMentions { UserIds = allowedUsers }
            );
            
            Console.WriteLine($"[Info] Private message sent to join channel for {user.Username}: {message.Substring(0, Math.Min(50, message.Length))}...");
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

    private async Task<string> LoadStoryAsync(ulong userId)
    {
        try
        {
            if (storyChannelId == 0) return "";
            
            var storyChannel = _client?.GetChannel(storyChannelId) as IMessageChannel;
            if (storyChannel == null) return "";

            Console.WriteLine($"[Story Load] Searching for story of user {userId} in story channel...");
            
            // البحث في آخر 100 رسالة في قناة القصص
            var messages = await storyChannel.GetMessagesAsync(100).FlattenAsync();
            
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

    private async Task<bool> CheckUserInStoryChannel(ulong userId)
    {
        try
        {
            if (storyChannelId == 0) return false;
            
            var storyChannel = _client?.GetChannel(storyChannelId) as IMessageChannel;
            if (storyChannel == null) return false;

            Console.WriteLine($"[Story Check] Checking if user {userId} has been mentioned in story channel...");
            
            // البحث في آخر 100 رسالة في قناة القصص
            var messages = await storyChannel.GetMessagesAsync(100).FlattenAsync();
            
            foreach (var message in messages)
            {
                // التحقق من وجود منشن للعضو في الرسالة
                if (message.MentionedUserIds.Contains(userId))
                {
                    Console.WriteLine($"[Story Check] Found mention for user {userId} in message {message.Id}");
                    return true;
                }
                
                // التحقق من وجود منشن للعضو في الـ Embeds
                foreach (var embed in message.Embeds)
                {
                    // البحث في محتوى الـ Embed عن منشن العضو
                    if (!string.IsNullOrEmpty(embed.Description) && embed.Description.Contains($"<@{userId}>"))
                    {
                        Console.WriteLine($"[Story Check] Found user {userId} in embed description");
                        return true;
                    }
                }
            }
            
            Console.WriteLine($"[Story Check] No mentions found for user {userId} in story channel");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Story Check Error] {ex.Message}");
            return false;
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
            await FollowupAsync("❌ حدث خطأ أثناء عرض القصة");
        }
    }

    private async Task<string> LoadStoryAsync(ulong userId)
    {
        try
        {
            // الحصول على معرف قناة القصص من المتغيرات البيئية
            var storyChannelIdStr = Environment.GetEnvironmentVariable("STORY_CHANNEL_ID");
            if (!ulong.TryParse(storyChannelIdStr, out ulong storyChannelId) || storyChannelId == 0)
            {
                return "";
            }
            
            var storyChannel = Context.Client.GetChannel(storyChannelId) as IMessageChannel;
            if (storyChannel == null) return "";

            Console.WriteLine($"[Story Load] Searching for story of user {userId} in story channel...");
            
            // البحث في آخر 100 رسالة في قناة القصص
            var messages = await storyChannel.GetMessagesAsync(100).FlattenAsync();
            
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
            await FollowupAsync("❌ حدث خطأ أثناء عرض معلومات الدعوة");
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

// Invite Information Class
public class InviteInfo
{
    public string InviterName { get; set; } = "";
    public ulong InviterId { get; set; }
    public string InviteCode { get; set; } = "";
    public DateTime JoinDate { get; set; }
}
