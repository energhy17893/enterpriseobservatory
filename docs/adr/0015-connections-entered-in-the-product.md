# ADR-0015: Bağlantılar üründen girilir, parola şifrelenmiş olarak saklanır

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-19
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0010 (kimlik bilgileri yapılandırma dosyasında bulunmaz),
  ADR-0005 (collector sözleşmesi), ADR-0011 (SQLite kalıcılık),
  ADR-0014 (yerel hesaplar), README ilke 5

## Bağlam

ADR-0010'a kadar bir vCenter'ın ürüne girmesinin tek yolu yapılandırmaydı:
user secrets, ortam değişkeni veya bir secret store. Bu karar doğruydu ve
doğru kalıyor — ama tek yol olması, ilk canlı denemede şunu üretti.

Parola bir kabuktan geçirildi. PowerShell çift tırnak içinde `$` ve ters
tırnağı yorumlar, dolayısıyla değeri **ürün görmeden önce** değiştirdi. Ürünün
verdiği cevap "vCenter kimlik bilgilerini reddetti" oldu — doğru, ve yanlış
parola yazmaktan ayırt edilemez. Arada geçen sürede yaklaşık yirmi bir
başarısız oturum açma denemesi yapıldı ve izleme hesabı kilitlenme sınırını
aştı.

Buradaki asıl mesele parolanın yanlış olması değil. Mesele, **kimlik bilgisinin
ürüne ulaşana kadar geçtiği katmanların sessizce değiştirebilmesi** ve ürünün
bunu söyleyecek hiçbir yolunun olmaması. Bir form katmanı yoktur: tarayıcıdaki
alan ne yazıldıysa onu gönderir.

İkinci mesele de bununla birlikte ortaya çıktı: kimlik bilgisini girdikten
sonra *çalışıp çalışmadığını* öğrenmenin tek yolu, toplama döngüsünün dört tur
sonra ürettiği bir alarmdı.

## Karar

### 1. Bağlantılar üründen girilebilir

Yönetici, bir vCenter'ı `Connections` ekranından ekler: ad, adres, kullanıcı,
parola, sertifika kararı, sayfa boyutu. Ad bir kez konur ve sonra kilitlenir —
değiştirmek yeniden adlandırma değil, o adla üretilmiş her varlığı öksüz
bırakmaktır.

### 2. Yapılandırma yolu aynen durur ve üstün gelir

Yapılandırmadan gelen bağlantılar çalışmaya devam eder, ekranda görünür ve
**düzenlenemez.** Aynı ada sahip bir kayıt varsa yapılandırma kazanır: servisi
dağıtan taraf daha yüksek otoritedir, ve saklanan bir satırın dağıtımı sessizce
ezmesi iki ekranın da gösteremeyeceği bir fark yaratırdı.

`CredentialSourceGuard` olduğu gibi yürürlüktedir. Ayar dosyasındaki bir parola
servisi hâlâ başlatmaz.

### 3. Parola veritabanında şifrelenmiş durur

ASP.NET Core Data Protection ile, amaç dizesi sürümlenmiş olarak
(`EnterpriseObservatory.SourceConnection.Password.v1`). Anahtar zinciri
veritabanının **yanında**, içinde değil.

### 4. Parola geri okunamaz

Parolayı döndüren bir uç yoktur ve olmayacak. Düzenleme formu alanı boş açar;
boş göndermek "saklı olanı olduğu gibi bırak" demektir. Bir "göster" düğmesi
talebi geldiğinde cevap hayırdır: geri okunabilen kimlik bilgisi, ekran
görüntüsünden, destek oturumundan ve fazla geniş bir hesaptan sızan kimlik
bilgisidir.

### 5. Kaydetmeden önce sınama

`Test` düğmesi formun içeriğiyle **tek bir** deneme yapar. Tekrar yok, geri
çekilme yok, ikinci bir yol yok. Sınayan bir düğmenin tekrar denemesi, hesabı
toplayıcının yapabileceğinden daha hızlı kilitlemenin yoludur.

Kimlik bilgisi reddedildiğinde mesaj bunu ayrıca söyler ve kilitlenme uyarısını
içerir — o mesajı okuyan kişi aynı düğmeye bir tık uzaklıktadır.

### 6. Kaynak listesi her turda yeniden okunur

Bileşim kökü artık toplayıcıları başlangıçta bir kez kurmaz. `ISourceRegistry`
her turda güncel listeyi verir; istemciler bağlantı ayarları değişmediği sürece
korunur, değiştiğinde bir tur gecikmeyle bırakılır (çalışan bir turun altından
istemcisini çekmemek için).

## Bunun koruduğu ve korumadığı şey

Bu bölüm ADR'nin en önemli kısmı, çünkü önceki ürün tam burada yanlış tarif
edildi.

**Korur:** çalınan bir veritabanı dosyasını. `observatory.db`'yi kopyalayan
biri parolaları alamaz.

**Korumaz:** ele geçirilmiş bir sunucuyu. Veritabanını *ve* anahtar
malzemesini okuyabilen ve servis hesabıyla çalışabilen biri parolalara sahiptir.
Bunu değiştiren hiçbir düzenleme yoktur: ürün, gece üçte kimse bir şey
yazmadan vCenter'a kimlik doğrulamak zorundadır, dolayısıyla orijinal değeri
gözetimsiz geri elde edebilmek zorundadır. Bunu yapabilen her şema tanımı
gereği tersine çevrilebilirdir.

Önceki üründen çıkarılacak ders "şifreleme başarısız oldu" değildir. Şifreleme
çalıştı. Başarısız olan, korumanın **kapsamı** ve bu konudaki **sessizlikti** —
parola, şifrelemenin kapsamadığı serbest metin bir alana serileştirilmişti ve
üründe bunu söyleyen hiçbir şey yoktu.

## Sonuçlar

### Yeni bir işletme yükümlülüğü

Veritabanı yedeği artık hassastır. Ve daha önemlisi:

> **Anahtar zinciri ile veritabanı aynı yedeğe konursa koruma tamamen
> kaybolur.** İkisi ayrı ayrı yedeklenmeli, ayrı erişim haklarıyla.

Bu, kabul edilen bir bedeldir, gözden kaçmış bir ayrıntı değil. Yedekleme
prosedürünü yazan kişinin bilmesi gerekir; bu yüzden kodda `KeyRingPath`
yorumunda ve burada iki kez yazılıdır.

### Anahtar kaybı servisi durdurmaz

Veritabanı anahtarsız geri yüklenirse bağlantılar yüklenir, "parola
çözülemiyor" der ve **sorgulanmaz.** Açılmayı reddetmek, tek bir okunamayan
sütun yüzünden tüm estate'in alarmını düşürürdü. Sorgulamak ise her otuz
saniyede bir başarısız oturum açma olurdu — yani ADR'nin engellemeye çalıştığı
şey.

### Parolası olmayan bağlantı sorgulanmaz

Boş parolayla denemek, garanti edilmiş bir reddedilmedir ve operatöre boş
parola alanının zaten söylediğinden fazlasını söylemez. Bunun yerine günlüğe
"şu bağlantı sorgulanmıyor, sebebi şu" satırı düşer — sessizlik sağlıklı
görünür, bu görünmemeli.

### Kabul edilen borç

- **Rotasyon hatırlatması yok.** `password_set_utc` saklanıyor ama kimse
  "bu parola 400 gündür değişmedi" demiyor.
- **Anahtar zinciri döndürme prosedürü yazılı değil.** Data Protection kendi
  anahtarlarını döndürür ve eskisiyle yazılanı okumaya devam eder; ama
  anahtarları kasıtlı olarak geçersiz kılmak isteyen birinin ne yapacağı
  belgelenmedi.
- **Windows dışında DPAPI yok.** Anahtar zinciri dosya sisteminde korumasız
  durur. Bugün ürün yalnızca Windows'a kuruluyor (ADR-0001, MSI), ama bu bir
  varsayım ve burada yazılı olması gerekiyor.
- **Bağlantı değişikliklerinin denetim kaydı yok.** Kimin eklediği saklanıyor;
  kimin değiştirdiği veya sildiği saklanmıyor. Alarm eylemlerinde (ADR-0013)
  olan disiplin burada henüz yok.
