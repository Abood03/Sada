# صدى · Sada

تطبيق ويندوز شخصي يعرض مؤثّرًا بصريًا يتفاعل مع الصوت، وغلاف الألبوم، وكلمات الأغنية المتزامنة كعناصر شفافة على سطح المكتب. يقرأ الأغنية ووقت التشغيل من جلسات الوسائط في ويندوز، ويعطي الأولوية لـ Spotify عندما يكون شغّالًا.

## التشغيل

نزّل الحزمة المحمولة **Windows x64** من صفحة الإصدارات، فكّ الضغط وشغّل `Sada.exe`. الحزمة تتضمن ‎.NET Runtime، والتشغيل اختُبر على Windows 11. [إصدارات GitHub](https://github.com/Abood03/Sada/releases/latest)

- من أيقونة صدى بجانب الساعة، اختر **Edit desktop scene** لترتيب العناصر. القوالب الجاهزة هي **Album** و**Karaoke** و**Focus**. استخدم **Save** للتطبيق، أو **Save as** لحفظ مشهد باسمه، أو **Cancel** لإلغاء المعاينة.
- شغّل أغنية في Spotify. عندما تظهر كلماتها، افتح **Media & lyrics → Edit this recording**. في **Lyrics Studio** اضبط بداية السطر عند سماعه، أو توقيت بداية ونهاية الكلمة، أو توقيت التسجيل كاملًا بخطوات 0.10 ثانية. عند دعم القفز من المشغّل، تقدر تعيد سماع مقطع للتحقق من التوقيت.
- يمكن جلب الكلمات تلقائيًا عبر **LRCLIB** أو فتح ملفات محلية، منها LRC وEnhanced LRC وLyricsfile/YAML. يظهر في محرّر الكلمات إذا كان توقيت الكلمة من المصدر أو تقديريًا أو معدّلًا. يحتفظ صدى بالتعديلات محليًا لكل تسجيل.
- لعرض كلمات YouTube، اختر **YouTube** من **Playback source**. يمكن استخدام الإضافة المرفقة لقراءة توقيت الفيديو مباشرة من Chrome أو Edge.
- من **Desktop widgets** اختر **Neon sign** لعرض عنوان الأغنية والفنان بألوان الغلاف على سطح المكتب أو في وضع ملء الشاشة.

## البناء من المصدر

ثبّت **.NET 10 SDK**، وافتح PowerShell في مجلد المستودع.

```powershell
dotnet build .\Sada.csproj -c Release
dotnet run --project .\Sada.csproj -c Release
```

يمكن إضافة `-- --tray` لبدء التشغيل بجانب الساعة. استخدم الأمر التالي لبناء الملف المحمول.

```powershell
dotnet publish .\Sada.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o .\artifacts\win-x64
```

يظهر `Sada.exe` داخل `artifacts/win-x64`. عند توزيع الحزمة، أرفق مجلد `Third-party` وملفات إضافة YouTube من مجلد `browser`. [توثيق ‎.NET للنشر كملف واحد](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

## أدلة المشروع

- [تاريخ الإصدارات](CHANGELOG.md)
- [البنية والتقنيات والبيانات المحلية](docs/architecture.md)
- [عرض كلمات YouTube في صدى](docs/youtube.md)
- [لوحة النيون وانتقالات العرض](docs/neon-and-motion.md)
- [تشغيل الاختبارات ونتائج التحقق](docs/testing.md)
- [المكتبات وتراخيص الأطراف الثالثة](THIRD-PARTY-NOTICES.md)
