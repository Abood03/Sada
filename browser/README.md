# إضافة صدى ليوتيوب

ترسل هذه الإضافة إلى صدى وقت التشغيل وحالة الإيقاف والقفز وسرعة التشغيل ومعرّف الفيديو وعنوانه. تعمل على صفحات المشاهدة في `www.youtube.com`، وتوقف إرسال حالة الأغنية أثناء الإعلان الذي يتعرّف عليه المشغّل. يستعمل صدى معلومات الأغنية للعثور على الكلمات وعرضها.

تحتاج الإضافة إلى تسجيل تطبيق **Sada YouTube Host** محليًا باسم `com.sada.youtube`. معرّف الإضافة الثابت هو `jafenianfjmlnaiepgbjeabccfdflpfd`. ينقل Chrome أو Edge الرسائل إلى هذا التطبيق عبر Native Messaging. [توثيق Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)

لاختبار الإضافة من المصدر، حمّل مجلد `browser` من صفحة إدارة الإضافات في Chrome أو Edge عبر **Load unpacked**، وأعِد تحميل تبويب يوتيوب المفتوح. شغّل صدى واختر مصدر YouTube. الإضافة مخصّصة لصفحات تشغيل الفيديو العادية.

اختبارات جافاسكربت محلية بلا تبعيات وتشغّل الرسائل وصفحة المشغّل في بيئة اختبار.

```powershell
node --test .\browser\tests\extension.test.cjs
```
