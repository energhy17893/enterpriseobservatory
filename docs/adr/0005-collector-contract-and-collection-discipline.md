# ADR-0005: Collector sözleşmesi ve toplama disiplini

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0003 (varlık modeli), ADR-0004 (ilişki taksonomisi)

## Bağlam

Ürün yedi satıcı entegrasyonuna sahip olacak (vSphere, iLO, iDRAC, SimpliVity,
OneView, OME, storage/SAN) ve zamanla artacak. Collector katmanı bu yüzden
ürünün en çok değişen ve en çok dış dünyaya temas eden parçası.

Önceki üründe collector'lar `IInfrastructureCollector` arkasında toplanmıştı ve
bu soyutlama **doğruydu** — yeni satıcı eklemek DI'a tek satırdı. Ancak üç şey
sözleşmede tanımlı değildi, dolayısıyla her collector kendi yorumunu yaptı:

1. **Envanter ve metrik aynı ritimde toplanıyordu.** 30 saniyelik döngüde hem
   tüm envanter baştan çekiliyor hem sayaçlar sorgulanıyordu. 2000 VM'lik bir
   ortamda değişmemiş nesneleri her döngüde yeniden okumak saf israf; buna
   karşılık bir host bağlantı kopukluğu en kötü ihtimalle 30 saniye sonra fark
   ediliyordu.
2. **Sunucu tarafı sorgu limitleri elle tahmin ediliyordu.** `entityIds.Chunk(32)`
   — sabitin nereden geldiği kayıtlı değil. vCenter'ın gerçek sınırı
   `config.vpxd.stats.maxQueryMetrics` ayarıdır (6.5+ varsayılanı 256) ve
   müşteri bunu değiştirebilir.
3. **Collector'ın ne yapmaya yetkili olduğu yazılı değildi.** Bazı collector'lar
   kimlik eşleştirme kararı verebiliyordu; bu, ADR-0003'teki tek-resolver
   ilkesiyle çelişir.

Ayrıca referans platformlardan öğrenilecek bir dayanıklılık deseni var:
VMware Aria Operations, collector örneklerini **collector grup**larına atar;
gruptaki bir düğüm düşerse diğeri toplamayı devralır. Bugünkü tek-proses
dağıtımında buna ihtiyaç yok, ama sözleşme buna kapalı olmamalı.

## Karar

### 1. Toplama iki ayrı akıştır

Bir collector tek bir "topla" metodu sunmaz. İki ayrı sorumluluğu vardır ve
bunlar **farklı ritimlerde** çalışır:

| Akış | Ne üretir | Ritim | vSphere karşılığı |
|---|---|---|---|
| **Envanter akışı** | Varlıklar, ilişkiler, özellikler | Olay güdümlü (mümkünse), yoksa seyrek poll | `WaitForUpdatesEx`, yoksa `RetrieveProperties` |
| **Gözlem akışı** | Metrik örnekleri | Periyodik | `QueryPerf` |

Gerekçe: envanter **durum**dur, değiştiğinde haber verilmesi yeterlidir.
Metrik **zaman serisidir**, değişmiyor olsa bile her aralıkta yeni örnek
üretilir. Bunları tek döngüye sıkıştırmak ikisini de bozar — envanter
gecikir, metrik israf eder.

`IInventorySource` ve `IObservationSource` ayrı portlardır. Bir collector
ikisini de uygulayabilir, ama `Application` katmanı onları ayrı ayrı sürer.

**İlk dilimde** envanter akışı poll tabanlı uygulanır
(`PollingInventorySource`). `WaitForUpdatesEx` tabanlı uygulama
(`ChangeFeedInventorySource`) ikinci dilimde gelir ve **adaptör değişimidir** —
`Application` katmanı hangisinin bağlı olduğunu bilmez. Bu, ADR-0001'deki
deployment-agnostik yaklaşımın collector katmanındaki karşılığı.

### 2. Sunucu limitleri keşfedilir, tahmin edilmez

Collector, hedef sistemin sorgu limitlerini **çalışma zamanında öğrenir** ve
batch boyutunu ona göre hesaplar. Sabit bir `Chunk(32)` yoktur.

vSphere için: `OptionManager` üzerinden `config.vpxd.stats.maxQueryMetrics`
okunur.

**Belirsizliğe karşı tasarım.** Bu ayarın *metrik* sayısını mı
(entity × sayaç) yoksa *entity* sayısını mı sınırladığı konusunda kaynaklar
çelişiyor. Bu belirsizliği "doğru yorumu bul" diye çözmüyoruz; **hangi yorum
doğru olursa olsun çalışan** bir algoritma kullanıyoruz:

```
budget      = okunan limit (okunamazsa 256 varsayılan; -1 ise sınırsız kabul et)
guvenlik    = 0.8
batchBoyutu = max(1, floor(budget * guvenlik / sayacSayisi))
```

Sayaç sayısına bölmek, katı yorumu (metrik sayısı) varsayar; yorum gevşekse
(entity sayısı) yalnızca gereğinden küçük batch kullanmış oluruz — doğru ama
biraz yavaş. Yanlış yönde hata yapmayız.

Buna ek olarak **uyarlanabilir geri çekilme** zorunludur: sorgu limit hatasıyla
dönerse (`Request processing is restricted by administrator` veya eşdeğeri)
batch boyutu yarılanır ve yeniden denenir; başarılı olan boyut oturum boyunca
hatırlanır. Böylece belgelenmemiş veya değişen limitler otomatik bulunur.

> Not: önceki üründeki `Chunk(32)`, 10 sayaçla çalıştırıldığında 320 metrik
> eder ve varsayılan 256 limitini **aşar**. Katı yorum doğruysa bu sorgular
> sessizce başarısız oluyordu. Bu, sabit sayının neden tehlikeli olduğunun
> somut örneği.

### 3. Collector'ın yetkisi ve yasakları

**Collector yapar:**
- Hedef sisteme bağlanır, kendi protokolünü konuşur
- Gördüğü şeyleri **gözlem** olarak bildirir: varlık adayları, kimlik
  işaretleri, ilişki adayları, metrik örnekleri
- Kendi sağlığını raporlar

**Collector yapmaz:**
- **Kimlik kararı vermez.** "Bu iLO kaydı şu ESXi host'tur" diyemez; yalnızca
  gördüğü kimlik işaretlerini (UUID, IP, FQDN, seri no, servis etiketi)
  bildirir. Eşleştirmeye `IdentityResolver` karar verir (ADR-0003).
- **Başka katmanın varlığını yaratmaz.** BMC collector'ı `EsxiHost` varlığı
  üretemez (README ilke 2). Üretebileceği varlık türleri sözleşmede kısıtlıdır.
- **İlişki tipi uyduramaz.** ADR-0004'teki altı tip dışına çıkamaz.
- **Veri uydurmaz.** Erişemediği bir şey için varsayılan, tahmini veya
  "simüle" değer döndüremez. Bilinmeyen `Unknown`'dır (README ilke 1).

Bu yasaklar mimari testlerle kısmen, kod incelemesiyle tamamen zorlanır.

### 4. Kısmi başarı birinci sınıf bir sonuçtur

Bir toplama çevrimi "başarılı" veya "başarısız" değildir. `CollectionOutcome`
üç şeyi birlikte taşır:

- Toplanan gözlemler
- **Toplanamayan** alanlar ve nedenleri (yetki reddi, zaman aşımı, sürüm
  uyumsuzluğu, yetersiz istatistik seviyesi)
- Collector sağlık durumu

Gerekçe: gerçek dünyada bir collector'ın yarısı çalışır. 20 iLO'nun 3'ü
erişilemezse, 17'sinin verisini atmak da 3'ünü sağlıklı saymak da yanlıştır.
Erişilemeyen 3 varlık `Unknown` olur ve bu **açıkça** raporlanır.

### 5. Hata izolasyonu ve dayanıklılık politikası

Bir collector'ın başarısızlığı diğerlerini veya döngüyü durduramaz. Bu, önceki
üründe doğru yapılmıştı ve korunuyor. Politika sözleşmeye yazılıyor:

| Kural | Değer | Gerekçe |
|---|---|---|
| Collector başına zaman aşımı | Poll aralığından türetilir, en az 10 sn | Yavaş bir satıcı döngüyü kilitlemesin |
| Geçici hatada yeniden deneme | En fazla 2, üstel geri çekilme + jitter | Gürültülü ağda gereksiz alarm üretme |
| Kalıcı hatada | Yeniden deneme yok, `Unknown` + Configuration bulgusu | Yetki reddi tekrar denemekle düzelmez |
| Ardışık başarısızlık | Devre kesici açılır, seyrek yoklama | Düşmüş bir endpoint'i her 30 sn dövmeyelim |
| Eşzamanlılık | Satıcı ailesi başına ayrı tavan | Bir vCenter'ı 50 paralel istekle boğmayalım |

Collector sağlığı **gözlemlenebilir bir varlıktır**, log satırı değil: kendi
durumu, son başarı zamanı ve ardışık hata sayısıyla grafta yaşar. Böylece
"izleme sisteminin kendisi sağlıklı mı" sorusu ürünün içinden cevaplanır.

### 6. Collector örneği ile collector çalıştırıcısı ayrıdır

Bir **collector örneği** bir hedefi temsil eder (şu vCenter, şu OneView
appliance'ı). Bir **çalıştırıcı** o örneği hangi proseste, hangi zamanlamayla
süreceğine karar verir.

Bugün tek bir çalıştırıcı var ve tek proseste çalışıyor. Ayrım şimdi
yapılıyor çünkü Aria Operations'ın collector-grup deseni (bir düğüm düşerse
diğeri devralır) ileride gerekirse **sözleşme değişikliği olmadan**
eklenebilsin. Örnek ile çalıştırıcıyı bugün birleştirirsek, yarın ayırmak
her collector'a dokunmayı gerektirir.

## Değerlendirilen alternatifler

### Alternatif A: Tek `CollectAsync` metodu (önceki ürünün yaklaşımı)

Basit, anlaşılır, az kod.

Seçilmedi çünkü envanter ve metriğin farklı ritimleri olduğu gerçeğini
gizliyor. Bu gizleme, önceki üründe hem vCenter'ı gereksiz yormaya hem de
durum değişikliklerini geç fark etmeye yol açtı. Tek metod, `WaitForUpdatesEx`
gibi push tabanlı bir kaynağı ifade edemez.

### Alternatif B: Batch boyutunu yapılandırma dosyasına almak

`Chunk(32)` yerine `"BatchSize": 32` — müşteri ayarlar.

Seçilmedi çünkü sorunu müşteriye devrediyor. Müşteri `maxQueryMetrics`
değerini bilmiyor, bilse bile sayaç sayısıyla çarpımını hesaplaması
beklenemez. Sistem kendi öğrenebiliyorken sormak kötü tasarımdır. (Yapılandırma
ile **üst sınır** koymak yine de mümkün olmalı — operatör kaçış kapısı.)

### Alternatif C: Collector'ın kimlik kararı vermesine izin vermek

Her collector kendi alanını en iyi bilir; iLO collector'ı iLO ile ESXi
eşleşmesini en iyi o bilir diye düşünülebilir.

Seçilmedi çünkü eşleştirme **collector'lar arası** bir karardır, tek bir
collector'ın bilgisiyle verilemez. Önceki üründe bu mantık dört ayrı yere
dağılmış ve birbirinden ayrışmıştı. Ayrıca bu yetki, README ilke 2'nin
ihlaline açık kapı bırakır.

## Sonuçlar

### Olumlu

- Envanter gecikmesi ile metrik maliyeti bağımsız olarak optimize edilebilir.
- `WaitForUpdatesEx`'e geçiş adaptör değişimi; `Application` katmanı değişmez.
- Sunucu limitleri otomatik bulunur; sahada "grafikler boş" sınıfı hatalar
  kendiliğinden düzelir.
- Kısmi başarı modellendiği için "17 iLO çalışıyor, 3'ü bilinmiyor" doğru
  ifade edilir.
- İzleme sisteminin kendi sağlığı üründen görünür.
- Ölçekleme için collector-grup deseni sözleşme değişikliği olmadan eklenebilir.

### Olumsuz / kabul ettiğimiz bedel

- **İki port, tek yerine.** Basit bir collector yazmak artık daha fazla
  ceremony istiyor. Küçük satıcı entegrasyonları için fazla gelebilir.
- Uyarlanabilir batch mantığı test edilmesi zor bir yol — sunucu limit
  hatasını taklit eden entegrasyon testleri gerekiyor.
- Devre kesici, gerçekten düzelen bir endpoint'in fark edilmesini geciktirir.
  Yarı-açık durum ve makul yoklama aralığı dikkatle seçilmeli.
- Collector sağlığını varlık yapmak grafa collector sayısı kadar düğüm ekler
  (önemsiz) ama "izleme varlıkları" ile "altyapı varlıkları" karışmasın diye
  sunum katmanında ayrılmalı.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Bir satıcı API'si envanter/metrik ayrımını anlamsız kılıyorsa (tek çağrıda
  ikisini birden veren bir REST ucu), sözleşmenin esnetilmesi değerlendirilir.
- Tek proses dağıtımı yetmediğinde collector-grup desenini somutlaştıran yeni
  bir ADR gerekecek.
