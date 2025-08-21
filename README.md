# Discord Onboarding Bot

بوت Discord مكتوب بـ C# لترحيب الأعضاء الجدد وإنشاء قصص مافياوية شخصية.

## المميزات

- ترحيب تلقائي بالأعضاء الجدد
- جمع معلومات شخصية عبر DM
- توليد قصص مافياوية باستخدام OpenAI
- حفظ القصص في ملف JSON
- نشر القصص في قناة مخصصة

## النشر على Render

### الخطوات:

1. **إنشاء حساب على Render**
   - اذهب إلى [render.com](https://render.com)
   - أنشئ حساب جديد

2. **ربط المشروع**
   - اضغط على "New +"
   - اختر "Web Service"
   - اربط حساب GitHub
   - اختر هذا المشروع

3. **تكوين المتغيرات البيئية**
   - في إعدادات الخدمة، أضف المتغيرات التالية:
     - `DISCORD_TOKEN`: توكن بوت Discord
     - `OPENAI_KEY`: مفتاح API الخاص بـ OpenAI

4. **النشر**
   - Render سيقوم تلقائياً ببناء وتشغيل المشروع
   - البوت سيعمل في الخلفية
   - يمكن الوصول للـ health check عبر `/` و `/health`

### المتغيرات البيئية المطلوبة:

- `DISCORD_TOKEN`: توكن بوت Discord من [Discord Developer Portal](https://discord.com/developers/applications)
- `OPENAI_KEY`: مفتاح API من [OpenAI](https://platform.openai.com/api-keys)

## التشغيل المحلي

```bash
# تثبيت المتطلبات
dotnet restore

# تشغيل البوت
dotnet run
```

## Endpoints

- `/`: رسالة "Bot is running!"
- `/health`: حالة اتصال البوت

## ملاحظات

- البوت يعمل في الخلفية كـ Background Worker
- Web Application تعمل على أي port متاح
- جميع وظائف البوت الأصلية محفوظة
- مناسب للنشر على Render Web Service




