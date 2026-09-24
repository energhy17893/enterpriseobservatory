# ADR-0026: Değerlendirme üç değerlidir; "bilinmiyor" bir alarmı çözemez

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-22
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0007, ADR-0009 (değerlendirme kapsamı), ADR-0018, ADR-0024,
  `proposals/alert-absence.md`, `reference-approaches.md` §6 ve §10.1

## Bağlam

Ürünün birinci ilkesi "asla uydurma"dır. 22 Eylül 2026'da C paketi, ürünün bu
ilkeyi alarm tarafında ihlal ettiğini gösterdi (#63): bir vCenter'ın metrik
okuması başarısız olunca o turda gözlem gelmiyor, ve `AlertLifecycle.OnAbsent`
onaylanmış bir alarmı **ilk kaçırılan turda** çözüyordu. Ürün bakmadığı bir şey
hakkında "düzeldi" diyordu. Aynı mekanizma her yeniden başlatmada bir düzine
alarmı yeniden açıp bildiriyordu.

İlke belgedeydi: `reference-approaches.md` §6 altı üründe "kural sustu"nun "her
şey yolunda"dan ayrıldığını yazmıştı, ve bu envanter kapsamında uygulanmış,
testle korunmuştu. Metrik kapsamı aynı fonksiyonu boş bir listeyle çağırıyordu,
ve yanındaki yorum bunu "bilinçli" diye anlatıyordu.

#63 o yolu kapattı. Ama kapatılan bir **örnek**; bugün bir kural hâlâ iki değer
döner (ateşledi / ateşlemedi), ve "ateşlemedi"nin "koşul yok" mu "bakamadım" mı
olduğu, çağıranın doğru listeyi geçirmesine bağlıdır.

Referans ürünler (§10.1) iki soruyu ayrı eksende tutuyor: Zabbix'te tetikleyicinin
**değeri** (OK/PROBLEM) ve **durumu** (normal/unknown) ayrıdır ve unknown üç
değerli mantıkla yayılır; Nagios'ta UNKNOWN ve UNREACHABLE ayrı durumlardır.
ADR-0024 aynı üçlüyü bulgu tarafına zaten getirdi (geçti / kaldı /
değerlendirilemedi).

## Karar

**Bir kuralın bir konu hakkındaki sonucu üç değerden biridir — koşul var, koşul
yok, bilinmiyor — ve alarm durum makinesinde "bilinmiyor"dan "çözüldü"ye geçiş
yoktur.**

1. Kural sonucu bir toplam tiptir. "Bilinmiyor" bir **sebep** taşır (kaynak
   cevap vermedi, sayaç toplanmıyor, seri yetersiz, girdi bayat).
2. Bir alarmı yalnızca **taze veriyle desteklenen "koşul yok"** çözer. Çözülme
   histerezislidir: ardışık N değerlendirme, N kural tipinin zorunlu
   parametresidir.
3. "Bilinmiyor" açık bir alarmı **açık tutar ve bayat diye işaretler**: alarm,
   kanıtının zamanını ve neden güncellenemediğini taşır. Ekran ve API tazelik
   olmadan durum gösteremez.
4. "Bilinmiyor" yeni bir alarm **açmaz**. Bakamamanın kendisi ayrı ve açık bir
   kuraldır (bugünkü `Collector unreachable`, `StorageLatencyBlindSpot`).
5. Alarmın kimliği ve başlangıç zamanı kalıcıdır; yeniden başlatmadan sonra ilk
   değerlendirmeden **önce** geri yüklenir; bildirilmiş bir alarm yeniden
   bildirilmez.
6. Susan bir kaynağın alarmları ileri taşınır; **ham saklama süresi (2 gün)
   dolunca alarm "bilinmiyor" durumuna geçer** — bkz. aşağıda.

### İleri taşımanın üst sınırı *(karara bağlandı, 22 Eylül 2026)*

Bir kaynak üç gün susarsa üç gün önceki CPU alarmı hâlâ "açık" görünür.
Referanslar dört ayrı cevap veriyor (§10.1): Prometheus çözer, Datadog son
durumu gösterir, Zabbix dondurur, Grafana ayrı bir NoData kaydı açar.

- **Seçenek 1 — süresiz taşı, bayat işaretle.** Hiçbir şey uydurulmaz; ama kutu
  eski kayıtlarla dolar.
- **Seçenek 2 — N saat sonra "bilinmiyor"a çevir.** Alarm kapanmaz, çözülmez;
  ayrı bir durum alır ve sayımlardan düşer. N bir seçimdir, alıntı değil.

**Karar: Seçenek 2, N = ham saklama süresi (2 gün).** O noktadan sonra alarmın
dayandığı örnekler de depoda yoktur, yani kanıt gerçekten kalmamıştır. Alarm
kapanmaz ve çözülmez; "bilinmiyor" durumunu alır, açık sayımından düşer, ve
kaynak geri döndüğünde ilk taze değerlendirme **yönü belirler**: taze "koşul
var" alarmı önceki alt durumuna döndürür (yeniden bildirilmez), taze "koşul yok"
çözülme sayacını 1'den başlatır — çözülme yine N ardışık taze "koşul yok" ister.
Araya giren bir "bilinmiyor" sayacı sıfırlar.
N bir **seçimdir**, alıntı değil: ham saklama süresine bağlıdır ve o süre
değişirse (ADR-0017) birlikte değişir.

### Sağlığa etkisi (ADR-0018'in uzantısı)

ADR-0018 sağlığı o nesneye açılmış en şiddetli alarmdan türetir. Bu ADR ona iki
durum ekler ve kuralı değiştirmez: **bayat** bir Critical alarm varlığı kırmızı
tutar, "şu tarihten beri" işaretiyle; **bilinmiyor** durumundaki bir alarm
varlığın sağlığını gri yapar, yeşil değil — ürün o varlık hakkında bir şey
bilmediğini söyler, iyi olduğunu değil. Bayatlama ve "bilinmiyor"a geçiş
bildirim üretmez; kaynağın susması zaten kendi alarmıyla ("Collector
unreachable") bildirilir.

## Gerekçe

- **İlke 1'in tipi.** "Bilinmeyen `Unknown`'dır" README'de yazıyor; bugün onu
  taşıyan bir tip yok, bu yüzden her çağıranın hatırlaması gerekiyor.
- **#63'ün sınıfını kapatır.** Üç değerli sonuçta boş liste geçirmek mümkün
  değildir; "bilinmiyor"u "koşul yok"a çevirmek derlenmez.
- **ADR-0024 ile simetri.** Bulgular zaten üç değerli; alarm ve bulgu aynı
  sözlüğü konuşur.

## Değerlendirilen alternatifler

### Alternatif A: #63'te kalmak

Sessiz kaynak listesini her yerde doğru geçirmek, testlerle korumak.

Seçilmedi çünkü bu, envanter kapsamında zaten yapılmış ve metrik kapsamında
unutulmuştu. Üçüncü bir kapsam (olaylar, bulgular, yarınki Redfish) aynı şekilde
unutur.

### Alternatif B: Prometheus modeli — durumsuz, `keep_firing_for`

Kural yalnızca koşulu söyler; yokluk ayrı sorgudur; çözülme bir gecikmeyle
yumuşatılır.

Seçilmedi çünkü gecikme bir yamadır: kaynak gecikmeden uzun susarsa alarm yine
okunmamış veriyle çözülür. `keep_firing_for`'un eklenme gerekçesi
([#11570](https://github.com/prometheus/prometheus/issues/11570)) bizim
hatamızın tarifidir.

### Alternatif C: Eksik veride alarmı "kritik"e yükseltmek (Nagios tazelik kontrolü)

Susan kaynak en kötüsü varsayılır.

Seçilmedi çünkü bu da uydurmadır, öbür yönde: bilmediğimiz bir şeyi bozuk ilan
eder. `Failed()`'ın `Critical` değil `Unknown` dönmesinin gerekçesi budur.

## Sonuçlar

### Olumlu

- Ürün bakmadığı bir şey hakkında "düzeldi" diyemez; yeniden başlatma alarm
  geçmişini bozmaz.
- Bayat alarm taze alarmla aynı görünmez.
- Alarm ve bulgu aynı üç değeri kullanır.

### Olumsuz / kabul ettiğimiz bedel

- `IAnalysisRule` sözleşmesi değişir; on üçten fazla kural ve testleri
  dokunulur. Çoğu mekanik, ama her kural "neyi bilmediğini" söylemeyi öğrenmek
  zorundadır.
- Alarm ekranı bir durum daha kazanır; "açık" sayısı artık iki sayıdır (taze,
  bayat).
- Çözülme N değerlendirme gecikir; operatör düzelmiş bir koşulu biraz daha açık
  görür.
- Üst sınır seçimi bir sayıdır ve alıntılanamaz; öyle işaretlenir.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Operatörler bayat alarmları gürültü bulursa — üst sınır kısaltılır, model
  değişmez.
- Bir kural türü için "bilinmiyor" anlamsız çıkarsa (saf olay kuralları: olay ya
  geldi ya gelmedi) — o tür iki değerli kalabilir, ama bunu tipi söyler.

## Uygulama notu (22 Eylül 2026)

Üç PR'de teslim edildi.

- **PR1 — çekirdek: `alert_history` ve model** (#83, `merge/adr-0026-core`,
  commit'ler `001f382` domain — üç değerli sonuç tipi (`SubjectVerdict`,
  `ConditionPresent`/`ConditionAbsent`/`Unknown`) ve `AlertLifecycle`'ın
  "bilinmiyor"dan "çözüldü"ye geçişi olmayan durum makinesi — ve `a202ae1`
  application — kural sözleşmesi ve N-derin çözülme). `AlertReconciler`'ın
  kanıt kaynaklı kararı (`EvidenceSources`, `ProducerRun`), Postgres şeması ve
  `alert_history` tablosu burada geldi. Susan bir kaynağın alarmlarının ileri
  taşınması ve ham saklama süresi (2 gün) dolunca "bilinmiyor"a geçişi de.
- **PR2 — envanter kuralları** (#85, `merge/adr-0026-rules`, alt PR #84
  `merge/pr2`). Envanter taraflı kuralları ve vSphere envanter kaynağını aynı
  sözleşmeye taşıdı.
- **PR3 — kalan sekiz kural** (#98, `merge/adr0026-pr3`, commit `ad51f44`).
  `peer-outliers`, `cpu-contention`, `storage-layer-split`,
  `shared-volume-latency`, `dropped-packets`, `storage-noisy-neighbour`,
  `storage-path-redundancy` ve `vcenter-events` mekanik `TwoValuedVerdicts`
  adaptöründen kuralın kendi yargısına (`Judge`) geçti — her biri kanıtsızken
  şunu sessizce çözüyordu:

  | kural | eskiden kanıt olmadan neyi çözüyordu |
  |---|---|
  | `peer-outliers` | politika tabanından az vantage noktası |
  | `cpu-contention` | eksik host/guest sayacı, okunamayan vCPU sayısı |
  | `storage-layer-split` | eksik katman sayacı; host fault-counter'a devredince o hosttaki her cihaz |
  | `shared-volume-latency` | 3'ten az bağlı host; boşta/meşgul bastırma |
  | `dropped-packets` | eksik paket sayacı; 100/s altı trafik |
  | `storage-noisy-neighbour` | 4'ten az ölçülen sakin; eksik yük sayacı; bilinmeyen taban çizgisi |
  | `storage-path-redundancy` | bakımdaki host; boş yol tablosu |
  | `vcenter-events` | belgelenmiş `ClearedBy`'ın temizlemesi hiç gelmemesi |

  Her satır artık gerçek bir "bilinmiyor" sebebi taşıyor
  (`InputNotCollected`, `NotJudgeable`, `InsufficientSeries`, `SourceSilent`),
  ve `AlertReconciler` N ardışık taze "koşul yok" olmadan çözmüyor.

Ölçülen gerçekler:

- **Kör-nokta histerezisi K=30, P=90**, canlı yeniden oynatmadan seçildi
  (`docs/live-verification.md` §11): 2,00 gün, 29 volume, 90.624 yargılanan
  tur üzerinden alarm çalkalanması 369,9'dan 5,0 flip/güne düştü;
  ölçülebilirlik %4,5; 385 gerçek kör dilimin %100'ü hâlâ yakalanıyor, en kötü
  durum 29,0 dakika.
- **Bu estate'te gözlemlenemezlik.** Dönüştürülen sekiz kuraldan yedisi bu
  estate'te hiç ateşlemedi — `maintenance_window` boş, bu yüzden örneğin
  `storage-path-redundancy`'nin "bakımdaki host" dalı hiç tetiklenmedi, ve
  öbür yedisinin koşulu da bu envanterde hiç oluşmadı. Dönüşümün değeri burada
  ölçülemez; değeri başka yerdedir — doğruluk, ve bir şey bozulduğunda artık
  neyin durmayacağı: eskiden bu sekiz kuralın her biri, eksik bir sayaç ya da
  küçük bir popülasyon karşısında alarmı sessizce çözerdi; artık çözmüyor.
## Uygulama notu (24 Eylül 2026) — bulgular ve yeniden başlatma

Yeniden başlatmadan sonra susan kaynakların bulguları yeniden yargılanmaz,
son hâlleriyle bayat taşınır (#187): varlık ayarları tasarım gereği
saklanmaz (`PostgresEntityGraphStore.cs` `Replace`, :67–72). Bir kontrolün
okuduğu açıklama ad alanı (ADR-0027; bugün yalnızca `BackupFreshnessCheck` →
`simplivity`) henüz cevap vermediyse bulgusu aynı şekilde taşınır; yalnızca
devre dışı ya da silinmiş bağlantıları olan bir ad alanı cevap vermiş sayılır.
