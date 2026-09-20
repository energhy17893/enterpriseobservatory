# Öneri: Analiz ve uygunluk katmanı

**Durum: öneri.** Karar verilmedi, kod yazılmadı. Bu belge, toplama katmanı
tamamlandıktan sonra bir sonraki katmanın nasıl kurulabileceğini anlatır ve
reddedilebilecek somut seçimler sunar.

Yazılma sebebi: [product-architecture.md](../product-architecture.md) §6 bu
motoru tarif ediyor ama şeklini tarif etmiyor. Veri artık mevcut olduğuna göre
şekil kararı verilebilir hale geldi — ve bu, "sen anlat, ben önereyim"
çalışma biçiminde benim tarafıma düşen kısım.

---

## 1. Neden şimdi

20 Eylül 2026 itibarıyla veri temeli tamam
([canlı doğrulama](../live-verification.md)):

- 8 620 seri, 30 saniyede bir, kayıpsız
- Depolama kimlik zinciri uçtan uca yürünebiliyor: VM → datastore → LUN → yol
- Cihaz ve yol başına ayrı seriler — "hangi LUN", "hangi yol" sorulabiliyor
- Sıkıştırma aritmetik olarak doğrulanmış, 400 günlük saatlik geçmiş mümkün

Yani merdivenin **basamakları** var. Eksik olan, basamakları **çıkan** şey.

## 2. Ne inşa edilmiyor

Önce kapsam dışını söylemek daha dürüst:

- **ML / anomali tespiti yok.** Mimari §11 bunu reddediyor: açıklanamayan bir
  tespit ilke 1'i ihlal eder. Bu öneri de reddediyor.
- **Genel amaçlı kural dili yok.** DSL, kullanıcının yazacağı kurallar, ifade
  değerlendirici — hiçbiri. Kurallar C#'ta, test edilebilir, gözden
  geçirilebilir. İlk on kural yazılmadan bir dil tasarlamak, hangi ifadelerin
  gerektiğini bilmeden sözdizimi seçmektir.
- **Dashboard tasarımı yok.** Ertelendi ve ertelenmiş kalıyor.

## 3. Bulgu, alarm değildir

Mimari §6 bunu söylüyor ve modellenmesi gerekiyor. Fark pratik:

| | Alarm | Bulgu |
|---|---|---|
| Ne der | "Şu an bir şey bozuk" | "Bu yapılandırma doğru değil" |
| Ne zaman kapanır | Koşul geçince kendiliğinden | Biri *düzeltince* |
| Susturma | Süreli sessizlik | **Süreli kabul** — kim, neden, ne zamana kadar |
| Aciliyet | Şimdi | Planlanabilir |

Süresiz kabul yasak: unutulmuş istisna, denetimde en pahalı bulgudur. Bu,
bakım penceresi disiplininin (ADR-0013) uygunluğa taşınmış hâli.

**Öneri:** `Finding`, `AlertInstance`'dan ayrı bir kayıt; kendi tablosu, kendi
yaşam döngüsü. Ortak olan `AlertFingerprint` benzeri kararlı bir kimlik ve
`OperatorIdentity` ile denetim izi.

## 4. Nerede durur

```
Collectors ──> InventorySnapshot / ObservationBatch
                        │
                  MonitoringCycle
                        │
        ┌───────────────┴────────────────┐
        │                                │
   AlertReconciler                 AnalysisEngine        ← yeni
   (mevcut, değişmiyor)            (kurallar, bulgular)
```

`Application/Analysis/` altında, hexagonal sınırları koruyarak. Motor
**envanter ritminde** çalışır (5 dakika), metrik ritminde değil: yapılandırma
30 saniyede bir değişmez ve bir kuralın metrik geçmişi okuması için canlı
örneğe ihtiyacı yok, depoya ihtiyacı var.

### Bir kural neyi görür

```csharp
public interface IAnalysisRule
{
    string Id { get; }              // "storage.latency-blind"
    string Title { get; }
    RuleSeverity Severity { get; }

    IEnumerable<Finding> Evaluate(AnalysisContext context);
}

public sealed record AnalysisContext
{
    EntityGraph Graph { get; init; }          // varlıklar, ilişkiler, kimlik işaretleri
    DateTimeOffset NowUtc { get; init; }
    ISeriesReader Series { get; init; }       // dar arayüz, IObservationStore değil
}
```

`ISeriesReader`, `IObservationStore`'un tamamı değil kasıtlı olarak: bir kural
yazmamalı, silmemeli, sıkıştırmamalı. Okuma bile sınırlı olmalı — aksi halde
bir kural 8 620 serinin 400 günlük geçmişini isteyebilir ve bunu fark etmeyiz.

**Açık soru (senin kararın):** okuma bütçesi nasıl sınırlanmalı? Öneri: kural
başına seri sayısı ve aralık genişliği üst sınırı, aşıldığında kural
çalıştırılmaz ve bunu *söyler* — sessizce atlanmaz.

## 5. İlk kurallar — hepsi bugünkü ölçümlerden

Sırası önem sırası. Her biri için: kanıt bugün veritabanında var.

### R1 · Bu estate 1 ms altını ölçemiyor
**Neden ilk sırada:** ürünün kendi körlüğünü söylemesi. Operatör "storage
gecikmesi 0 ms" grafiğine bakıp depolamayı eliyor; oysa ürün ona hiçbir şey
söylememiş.

**Koşul:** `siocActiveTimePercentage` sürekli 0 **ve** gecikme sayaçları
sürekli 0 **ve** IOPS sayaçları 0'dan büyük.
**Bulgu:** "Depolama gecikmesi 1 ms altında ölçülemiyor. SIOC'u etkinleştirin."
**Bugünkü veri:** 302 host-volume çiftinin tamamı bu koşulda.

### R2 · Bir volume tek host'tan yavaş
**Neden önemli:** merdivenin ayırt edici basamağı. Her host'tan yavaşsa dizi
veya fabric; tek host'tan yavaşsa o host'un HBA'sı, kablosu ya da yolu.

**Koşul:** bir datastore'un `totalReadLatency` serilerinden birinin p-yüksek
değeri, diğerlerinin medyanının belirgin katı.
**Bugün mümkün mü:** evet — datastore başına host başına ayrı seri var.

### R3 · Yol hatası
**Koşul:** `storagePath.busResets` veya `commandsAborted` sıfırdan büyük.
**Eşik yok:** bu bir arıza sinyali, ayar değil. SCSI, dizi meşgul diye bus
reset atmaz.
**Bugünkü veri:** 7 608 örnekte sıfır — yani bu estate temiz ve ürün bunu
söyleyebilir.

### R4 · Cluster HA / DRS kapalı
**Koşul:** `HighAvailabilityEnabled == false` (null **değil**).
**Dikkat:** null, "okuyamadık" demek ve zaten ayrı bir alarmı var. Bu ayrımın
korunması şart — "HA kapalı" demek, bakmadığın bir cluster için en pahalı
yanlıştır.

### R5 · Yayılmış volume tek path grubunda
`StorageDeviceId` işaretleri artık birden fazla olabiliyor; bir volume'ün
extent'lerinden biri diğerlerinden farklı yol sayısına sahipse bu bir zoning
tutarsızlığıdır.

**Not:** bu kural, bugün kapatılan kimlik zinciri olmadan yazılamazdı.

## 6. Kural motoru, eşik motorunun üst kümesi (§6)

Mimari bunu şart koşuyor: ikisi ayrı yazılmamalı. Ama bugün toplayıcılar zaten
alarm üretiyor (datastore doluluğu, host erişilemez, cluster yapılandırması
okunamıyor, vCenter alarmları).

**Öneri — sınır şurada:**

| Toplayıcı alarmı | Motor bulgusu |
|---|---|
| Yalnızca toplayıcının bilebileceği şey | Grafiğe, geçmişe veya birden fazla nesneye ihtiyaç duyan yargı |
| Tek nesne, toplama anında | Çapraz varlık, çapraz metrik, zaman içinde |
| "vCenter bu datastore'u erişilemez bildiriyor" | "Bu volume tek host'tan yavaş" |

Bu bir uzlaşma değil, ilkeli bir çizgi: toplayıcı *gözlemi* raporlar, motor
*yargıyı* verir. Mevcut alarmlar taşınmaz — taşınmaları için bir sebep
çıkarsa ayrı bir karardır.

**Bunu kabul etmiyorsan** alternatif nettir: toplayıcılar alarm üretmeyi
tamamen bırakır, her şey motora taşınır. Daha saf, ama bugün çalışan ve canlıda
doğrulanmış beş alarmı yeniden yazmak demek.

## 7. Saklama ve sunum

- Bulgular `finding` tablosunda, `alert_instance` ile aynı veritabanında.
- Kabuller `finding_acceptance`: kim, ne zaman, neden, **ne zamana kadar**.
- Süresi dolan kabul, bulguyu kendiliğinden geri getirir — sessizce değil,
  "kabul süresi doldu" diyerek.
- Arayüzde alarmlardan **ayrı bir sayfa**. Bulguların aciliyeti yok; inbox'a
  karışmaları, inbox'ın anlamını bozar.

## 8. Karar verilmesi gerekenler

Bunlar benim değil, senin kararların:

1. **Sınır (§6) kabul mü?** Toplayıcı alarmları yerinde mi kalsın, yoksa her
   şey motora mı taşınsın?
2. **Bulgu şiddeti** alarm şiddetiyle aynı sözlüğü mü kullansın
   (Critical/Warning), yoksa kendi sözlüğü mü olsun (ör. High/Medium/Low +
   Informational)? Aynı sözlük karıştırmayı kolaylaştırır; ayrı sözlük iki
   kavramı ayrı tutar.
3. **Okuma bütçesi** nasıl sınırlansın?
4. **İlk kural kümesi** R1–R5 mi, başka bir öncelik mi?
5. **Kabul süresi** üst sınırı olmalı mı? (ör. en fazla 90 gün, sonra yeniden
   gerekçelendir)

Bu belgenin kapsamı dışında ama aynı anda bekleyen iki karar daha:

6. **Sağlık yayılımı birleştirme fonksiyonu.** ADR-0004 yönü karara bağladı,
   birleştirmeyi bağlamadı; `PropagatesHealth`'in üretimde çağıranı yok ve
   cluster'lar kalıcı olarak `Unknown`. On host'tan biri kritikken cluster ne
   olmalı — kritik mi, düşmüş mü, yoksa HA görevini yaparken hiçbir şey mi?
   (Bkz. [canlı doğrulama](../live-verification.md).)
7. **Saklama ve disk.** Kararlı durumda ≈ 30 GB; bunun 17 GB'ı `storagePath`.
   Kabul mü, saklama kısalt mı, yoksa yol gecikmesini bırak mı?

## 9. Yapılmaması önerilen ilk adım

Motoru yazıp sonra kural aramak. Ters sırası doğru: **R3 (yol hatası) tek
başına, motorsuz yazılabilir** ve yazılmalı — çünkü bir kuralın gerçekten neye
ihtiyaç duyduğunu ancak bir kural yazınca öğreniriz. İkinci kural neyin ortak
olduğunu gösterir; motor üçüncüde çıkarılır.

Bu, bu projede tekrar tekrar işe yarayan disiplinin aynısı: **önce ölç, sonra
tasarla.** Bir soyutlamayı, onu haklı çıkaracak üç örnek görmeden çıkarmak da
aynı hatanın başka biçimi.
