# ADR-0011: Durum kalıcılığı için gömülü SQLite

- **Durum:** Geçersiz kılındı — yerini ADR-0016 aldı
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001 (dağıtım kısıtı), ADR-0004 (varlık yaşam döngüsü),
  ADR-0005 (collector sözleşmesi), ADR-0009 (değerlendirme kapsamı),
  ADR-0010 (kimlik bilgileri)

## Bağlam

`Host.AllInOne` durumu bellekte tutuyordu ve bunun bedeli üç ayrı yerde
ödeniyordu. Üçü de "yeniden başlatma" gibi nadir bir olaya bağlı görünüyor ama
bir Windows servisi güncelleme, yama ve yeniden başlatma yüzünden ayda birkaç kez
yeniden başlar.

**1. Alarm durumu.** En ağırı. Yeniden başlatma her onayı, her operatör
kapatmasını ve neyin zaten bildirildiğini unutuyordu. Ürün ayağa kalkıp otuz
tane uzun süredir devam eden problemi yeni sanıyor ve hepsi için birini
uyandırıyordu. Bundan sonra kimse bildirimlere güvenmez — README ilke 4'ün
("gürültü operatörün düşmanıdır") tam tersi.

**2. Kaybolma saati.** ADR-0004 kaybolmuş bir varlığı otuz gün saklıyor ve bu
süre **son görülme** anından işliyor. Bellekte tutulduğunda her yeniden başlatma
o saati sıfırlıyordu; mezar taşı hiç dolmuyor, bir bakım penceresini veya
donanım değişimini atlatması gereken geçmiş atlatamıyordu.

**3. Devre kesici sayacı.** Arka arkaya hata sayısını unutan bir proses, bütün
gün kendisini reddeden bir collector'a yeniden yükleniyordu. Yalnızca hız
sınırına takılmış bir hesabın izleme aracı yüzünden kilitlenmesi, ilke 5'in
("izleme aracı üretimi bozmaz") doğrudan ihlali.

## Karar

### 1. Gömülü SQLite, dosya olarak

ADR-0001 ek bir appliance gerektirmeyen bir MSI istiyor. Kimsenin
sağlamak zorunda olmadığı bir veritabanı, operatörün bir bakım penceresinde
kurduğu ürün ile proje gerektiren ürün arasındaki fark.

Veritabanı `ProgramData` altında bir dosya; kurulum dizininde değil, çünkü
yükseltmeden sağ çıkması gereken veri yükseltmenin yazdığı yere konmaz. Yol
yapılandırılabilir.

Çok düğümlü dağıtımda SQLite yetmeyecek. O gün geldiğinde port arkasındaki
uygulama hiç değişmeden başka bir adaptör yazılır; bugün olmayan bir ihtiyacı
bugünden çözmek, ölçmediğimiz bir şeklin tahminini kod haline getirmek olurdu.

### 2. Elle yazılmış ilişkisel şema, ORM değil

Tablolar açık, migration'lar numaralı ve birisinin incelediği bir diff. Önceki
üründe hiç versiyon kontrolü yoktu ve "ne değişti, neden" cevapsızdı; başlangıçta
yansımayla kendini kuran bir şema, yanlış olmanın kalıcı olduğu yerde tam olarak
bunu geri getirirdi.

Alan modeli kayıtlar halinde ve iç içe koleksiyonlar taşıyor; bunları tek bir
JSON sütununa koymak daha az kod olurdu. Yapılmadı: ADR-0007'nin **Kanıt**
grubunun sorusu "bunu nasıl ispatlarım" ve bir JSON blob'u sorgulanabilir değil.
Dosyayı bir veritabanı tarayıcısıyla açan birinin ne olduğunu görebilmesi
gerekiyor.

Numaralandırmalar **isimleriyle** saklanıyor, sayılarıyla değil. Sayı daha küçük
ve biri enum'un ortasına bir değer eklediği gün sessizce başka bir şey demeye
başlıyor; isim ya gürültülü şekilde yanlış ya da doğru. API'nin string enum'ları
ile aynı gerekçe, daha yüksek bahisle — bu veri prosesten uzun yaşıyor.

Bilinmeyen bir isim okunduğunda **istisna atılıyor**, varsayılana düşülmüyor.
Varsayılan sessiz bir yalan olurdu: severity'si okunamayan bir alarm Info'ya
dönüşür ve gelen kutusundan kaybolurdu.

### 3. Yazma birimi bir döngünün kararıdır, bir satır değil

Her yazma bir transaction. Bir uzlaştırma bütün bir kapsam hakkında tek bir
karardır; yarısının uygulanması ne yanan ne çözülmüş alarmlar bırakır — hiçbir
kodun doğru okumadığı bir durum, çünkü hiçbir kod onu beklemiyor.

Kapsam bazlı yazma ADR-0009'un kuralını SQL'e taşıyor: bir değerlendirme yalnızca
kendi kapsamını siler ve yeniden yazar.

### 4. Veritabanı kayıt, bellek çalışma kopyası

Graf her API isteğinde ve her döngüde okunuyor. Her seferinde birkaç bin satırı
yeniden okumak kalıcılığı bir dayanıklılık kararı olmaktan çıkarıp performans
kararına çevirirdi. Tek yazıcı proses var ve her yazma bu depolardan geçiyor, o
yüzden kopya ayrışamaz — ve taze bir örneğin yazılanı geri okuduğunu ispatlayan
bir test var.

### 5. Testler gerçek uygulamayı kullanır

Elle yazılmış bir bellek-içi depo, adım adım tutulması gereken ikinci bir
semantik olurdu ve ilk ayrışacak şey tam da önemli olan incelikler olurdu: bir
yazmanın hangi kapsamı değiştirdiği, emeklilerin gerçekten uygulanıp
uygulanmadığı. Onun yerine testler aynı uygulamayı bellek-içi bir veritabanına
karşı çalıştırıyor.

Yeniden başlatma testleri ise **gerçek dosya** kullanıyor: bellek-içi bir
veritabanı tam da son bağlantı kapandığında yok olur, yani bir gidiş-dönüşü
ispatlayabilir ama bir yeniden başlatmayı asla.

### 6. Zaman serisi bu ADR'nin kapsamında değil

Metrikler ekleme-ağırlıklı, yüksek hacimli ve aralık sorgulanan bir yük; alarm ve
graf durumu ise küçük, işlemsel ve oku-değiştir-yaz. Bunları aynı kararla
çözmeye çalışmak ikisini de kötü çözer.

Saklama süresi, aşağı örnekleme ve toparlama (rollup) kendi ADR'sini hak ediyor
ve o kararlar ölçüm gerektiriyor. Bugünkü kısıt şu: gözlemler henüz hiçbir yere
yazılmıyor, yani **Analiz** grubu ("nereye gidiyoruz") hâlâ boş.

## Sonuçlar

### Olumlu

- Onay, operatör kapatması ve bildirim geçmişi yeniden başlatmayı atlatıyor.
- Mezar taşı saati gerçekten işliyor; ADR-0004 artık yalnızca yazılı değil.
- Devre kesici sayacı korunuyor, yani yeniden başlatma bir hesabı kilitlemiyor.
- Denetim izi (hangi geçiş, kim, ne zaman) kalıcı.
- Dosya tek başına taşınabilir: destek için bir kopya almak `copy` kadar basit.
- Kurulum yüzeyi değişmedi — hâlâ tek MSI, hâlâ servis yok.

### Olumsuz / kabul ettiğimiz bedel

- **Her döngü kapsamı baştan yazıyor.** Basit ve doğru; büyük bir ortamda
  envanter döngüsünün bütün grafı yeniden yazması pahalıya gelebilir. Fark
  (diff) yazmak bir sonraki adım, ama önce ölçmek gerekir — bugün tahminle
  optimize etmek, ADR-0005'te sabit `Chunk(32)` ile yapılan hatanın aynısı olur.
- **Bellek kopyası ile disk ayrışabilir**, eğer bir gün ikinci bir yazıcı
  eklenirse. Bugün tek yazıcı var; çok prosese geçiş bu varsayımı açıkça
  yeniden değerlendirmek zorunda.
- **SQLite tek yazıcıya izin verir.** Bugünkü yük (30 saniyede bir ve 5 dakikada
  bir birer transaction) buna fazlasıyla uyuyor, ama bu bir tavan.
- **Şema migration'ları elle yazılıyor.** Daha fazla emek, ve bir migration
  yanlışsa bedeli kalıcı. Karşılığında ne olduğu okunabilir.
- `SQLitePCLRaw` bağımlılığı `Microsoft.Data.Sqlite`'ın istediğinin üstüne
  sabitlendi: geçişli 2.1.11 sürümü GHSA-2m69-gcr7-jv3q taşıyor ve NuGet denetimi
  build'i kırıyor — ki denetimin amacı bu. Bastırılmadı, sürüm yükseltildi.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Toplama ile web ayrı proseslere bölündüğünde: iki yazıcı, SQLite'ın kabul
  ettiği modeli aşar ve paylaşılan bir depo yeni bir ADR gerektirir.
- Zaman serisi eklendiğinde: ölçümler aynı dosyaya mı gidecek, yoksa kendi
  deposuna mı, ayrı bir kararla verilecek.
- Bir kurulum SQLite'ın yazma kapasitesini zorlarsa — o noktada ölçüm var
  demektir ve karar tahmine değil veriye dayanır.
