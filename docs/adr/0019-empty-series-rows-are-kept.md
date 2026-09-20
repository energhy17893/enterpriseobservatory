# ADR-0019: Boş seri satırları silinmez, birikmesine izin verilir

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-20
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0012 (sıkıştırma geçişi), ADR-0016 (PostgreSQL),
  ADR-0017 (saklama pencereleri), README ilke 1

## Bağlam

Sıkıştırma geçişi (`PostgresObservationStore.Compact`) beş adımdı: iki katlama,
iki retention silmesi ve son olarak **`ForgetEmptySeries`** — hiçbir
çözünürlükte verisi kalmamış `series` satırlarını silen bir adım.

Bu son adım bir amaç için yazılmıştı: `series` tablosu servis açılışında
**tamamen belleğe** yükleniyor (`_seriesIds`), çünkü her örneğin bir seri
kimliğine ihtiyacı var ve bunu döngü başına 6 084 kez sormak kabul edilebilir
değil. Satırlar birikirse sözlük de birikir.

Ama aynı adım, bu dalda düzeltilen kusurun kaynağıydı:

- `sample.series_id` bir yabancı anahtardır ve **CASCADE** eder.
- Silme, metrik döngüsü yazarken düşerse yalnızca çakışmaz: **kazanırsa o anda
  yazılmış örnekler satırla birlikte sessizce gider.** Eski davranışta yabancı
  anahtar ihlali `PostgresDatabase.Write` işlemini geri alıyordu ve o döngünün
  **tüm estate için** örnekleri kayboluyordu — tek bir hurdaya çıkmış makine
  yüzünden 30 saniyelik boş geçmiş.
- `FOR UPDATE SKIP LOCKED` (süpürme tarafı) ve işlem içi kimlik bağlama
  (`BindSeries`, ekleme tarafı) bunu güvenli hale getirdi.

Yani ortada güvenli hale getirilmiş bir adım vardı. Sorulan soru şuydu:
**bu adım ne kazandırıyordu?**

### Ölçüm

`series` satırı altı kısa sütundur (`id`, `entity_id`, `counter`, `instance`,
`unit`, `rollup`). Gerçek satır genişlikleriyle:

| | Satır başına |
|---|---|
| Yığın satırı (başlık + sütunlar + item pointer) | ~148 bayt |
| `ux_series` (entity_id, counter, instance) | ~104 bayt |
| `ix_series_entity` | ~32 bayt |
| **Disk toplamı** | **~300 bayt** |
| Bellek: sözlük düğümü + `SeriesKey` + `EntityId` + üç string | **~370 bayt** |

Karşılaştırma için ADR-0017'nin ölçtüğü değerler: `sample` satırı 92,2 bayt,
`bucket` satırı 173,1 bayt ve bir seri 5 760 + 8 640 + 2 160 = 16 560 satır
taşıyor. Bir seri satırı **silinebilir hale geldiğinde**, onu pahalı yapan
16 560 satır zaten gitmiştir. Silme, 300 baytı geri alır.

### Birikim ne kadar hızlı?

Ölçülen estate: 200 varlık, 6 084 seri → **varlık başına ~30 seri.** İki kaynak
var ve büyük olan, beklenen olan değil:

**1. Yapılandırmayla durdurulan sayaçlar.** 20 Eylül'de `storagePath` çifti
bırakıldığında **2 536 seri** bir öğleden sonrada yetim kaldı
([live-verification](../live-verification.md) §7). Bu ~1,7 MB'dır (disk +
bellek), bir kere ve temelli. Zamanla değil, sayaç kataloğuyla sınırlıdır.

**2. Hurdaya çıkan varlıklar.** Yılda %10-20 makine değişimi → 20-40 varlık →
**yılda 600-1 200 satır** → yılda 1 MB'ın altı, on yılda ~5 MB.

Toplam disk 13,6 GB. Yıllık birikim bunun **on binde birinden azı.**

## Karar

**`ForgetEmptySeries` kaldırıldı. Boş seri satırları silinmiyor; birikmelerine
bilerek izin veriliyor.**

Kapsam:

- Sıkıştırma geçişi artık dört adımdır. `CompactionSequence.Run` beşinci bir
  delege almaz.
- `CompactionReport.SeriesForgotten` alanı ve onu yazan log parametresi de
  kaldırıldı — her geçişte "0 seri unutuldu" yazmak, ürünün kendisi hakkında
  söylediği küçük bir yalandır.
- `Append` içindeki işlem içi kimlik bağlama (`BindSeries`, `FOR KEY SHARE`)
  **kalıyor.** O yalnızca bu süpürmeye karşı değil; yedekten dönüş, elle
  yapılan bir temizlik ve ileride eklenecek herhangi bir süpürme aynı boşluğu
  açar.
- Yerinde bir yorum bırakıldı. Adımın yokluğu, yapılmamış bir iş gibi
  görünmemeli.

## Gerekçe

**1. Hiçbir şey geri kazanmıyordu.** 300 bayt, geri kazanılması için 16 560
satırın önce silinmesini gerektiren bir kazançtır.

**2. Asıl kazandırdığı şey başka yoldan alınabilir.** Küçük bir açılış sözlüğü
istiyorsak, doğru araç tembel yükleme veya sınırlı bir önbellektir — ekleme
yolunun üstüne zamanlayıcıyla `DELETE` koymak değil.

**3. Kilit paylaşımı, kazancı olmayan bir risk.** Güvenli hale getirilmiş
olması, orada olmasını haklı çıkarmıyor. Sıfır kazanç için sıcak yolla kilit
paylaşan bir geçiş, her okuyucunun yeniden doğrulaması gereken bir ilişki
bırakır. **Hiçbir şey kazanmamak için satır silmek, ekleme yoluna yabancı
anahtar yarışını sokan şeyin ta kendisiydi.**

## Değerlendirilen alternatifler

### A. Adımı güvenli haliyle bırakmak
Bu daldaki düzeltme onu zaten güvenli yapmıştı; hiçbir şey yapmamak bedava
görünüyordu. Seçilmedi, çünkü bedava değil: `SKIP LOCKED` iddiası, işlem içi
bağlama ile arasındaki ilişki ve "silinen satır hangi işlemin altından
çekiliyor" sorusu kalıcı bir okuma maliyetidir — ve karşılığında yılda 1 MB
var. Bir mekanizma, ancak kazandırdığı şey kadar karmaşıklığı hak eder.

### B. Adımı bırakıp sözlüğü tembel yüklemeye çevirmek
Doğru uzun vadeli cevap ve bir gün gerekebilir (aşağıdaki sinyale bakınız).
Bugün yapılmadı: tembel yükleme, bugün açılışta çözülen bir maliyeti döngünün
içine dağıtır ve ölçülmeden yapılacak bir iş değildir. Ölç, sonra öner. Bugünkü
rakam bu işi gerektirmiyor.

### C. Yetim satırları elle/ara sıra temizleyen bir bakım komutu
Silmeyi sıcak yoldan çıkarır ve bir gün eklenebilir. Bugün eklenmedi, çünkü
olmayan bir problem için bir araç yazmak olurdu; ve asıl birikim kaynağı (bir
yapılandırma değişikliği) zaten tek seferliktir.

## Sonuçlar

### Olumlu
- Sıkıştırma geçişi `series` tablosuna hiç dokunmuyor. Ekleme yoluyla kilit
  paylaşan bir silme kalmadı.
- Geçiş dört adım, `CompactionSequence` bir delege daha kısa.
- Ürün artık yapmadığı bir iş hakkında sayı raporlamıyor.

### Olumsuz / kabul ettiğimiz bedel
- **Boş seri satırları temelli birikir.** Ölçülen estate'te bugün 2 536 satır
  (~1,7 MB) ve yılda 600-1 200 satır (~1 MB). On yılda ~5 MB.
- `series` tablosu 8 620'de kalır; live-verification'ın "90 gün içinde 6 084'e
  inecek" cümlesi artık geçersizdir ve orada düzeltildi.
- Açılış sözlüğü yalnızca büyür. Hiçbir şey ondan giriş silmez.
- Silinmiş bir varlık 30 gün sonra varlık grafiğinden unutulur
  (`EntityGraph`), ama seri satırları kalır. Bu asimetri bilinçlidir: ölçüm
  gitmiştir, yalnızca adı duruyor.

### Bu kararı yeniden değerlendirmemiz gereken durum

Tek ve net bir sinyal: **varlıkları saklanan değil, yeniden yaratılan bir
estate.** Kalıcı olmayan 200 VDI masaüstü her gece yeniden kurulursa günde
6 000 yeni seri demektir — yılda 2,2 milyon satır, ~800 MB sözlük, ve bunların
hepsi servis ilk isteği cevaplamadan önce belleğe yükleniyor. Aynı hesap
CI/CD'nin geçici makineleri ve otomatik ölçeklenen havuzlar için de geçerlidir.

Somut eşik: sözlüğün 100 MB'a ulaşması ~270 000 satır demektir. Normal makine
değişiminde bu **iki yüz yıldan** uzun; kalıcı olmayan VDI'da **kırk beş gün.**
O sinyal geldiğinde cevap bu adımı geri koymak değil, alternatif B'dir — tembel
yükleme veya sınırlı önbellek. Süpürme, sorunun bellek tarafını çözerken sıcak
yola kilit getiriyordu; tembel yükleme aynı sorunu ekleme yoluna hiç dokunmadan
çözer.
