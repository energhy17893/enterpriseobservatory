# Canlı Doğrulama Kaydı

Bu belge, ürünün **gerçek bir estate'e karşı** neyi kanıtladığını kaydeder.
Yeşil birim testi bir kanıt değildir: bu projede bugüne kadar bulunan
hataların çoğu, testleri geçen ve canlıda sessizce yanlış olan kodda çıktı.

Her giriş şunu söyler: **ne ölçüldü, hangi sayılarla, nasıl tekrar edilir.**
Tekrar edilemeyen bir doğrulama, hatırlanan bir doğrulamadır.

## Doğrulama ortamı

| | |
|---|---|
| vCenter | vSphere 8, 732 sayaç tanımlı |
| Envanter | 10 ESXi host, 145 VM, 41 datastore, 3 cluster |
| Depolama | Pure (all-flash) + yerel diskler, FC fabric |
| İstatistik seviyesi | 3 (seviye 2 ürünün şartı; bu estate daha yüksek) |
| Veritabanı | PostgreSQL 18.6, `observatory` rolü (superuser değil) |

---

## 1. Uçtan uca zincir: vCenter → PostgreSQL → ekran

**20 Eylül 2026.** Servis canlı vCenter'a bağlandı, 200 varlık topladı,
PostgreSQL'e yazdı, arayüzde gösterdi.

```
Inventory cycle: 200 active, 0 vanished, 1 alerts visible.
```

Veritabanı ile ekran birebir tutuyor: `Critical 1 · Warning 0 · Healthy 189 ·
Unknown 10` = 200.

---

## 2. Sayaç toplama

| | Ölçüm |
|---|---|
| Toplam seri | 8 620 |
| Host başına seri | 612–615 |
| Datastore başına seri | 60 (6 sayaç × 10 host) |
| VM başına seri | 5 |
| Örnekleme | 30 saniyede bir, seri başına kayıpsız |

`virtualDisk.total{Read,Write}Latency` 138 VM'e yazıyor (145 VM'in 7'si kapalı;
kapalı VM'ler `Unknown` sağlıkta, "sağlıklı" değil).

## 3. Depolama kimlik zinciri — **tam**

41/41 datastore hem `VolumeIdentifier` (VMFS UUID) hem `StorageDeviceId`
(LUN NAA) işareti taşıyor. Zincir tek SQL sorgusuyla yürünebiliyor:

| Datastore | VM | Datastore serisi | LUN cihaz serisi | Yol serisi | Ayrı initiator WWPN |
|---|---|---|---|---|---|
| PRODVOL10 | 14 | 60 | 1 | 80 | 20 |
| PRODVOL3 | 13 | 60 | 1 | 80 | 20 |
| PRODVOL8 | 12 | 60 | 1 | 80 | 20 |

Yani *"şu VM yavaş → hangi datastore → hangi LUN → 10 host × 2 HBA = 20 yoldan
hangisi"* sorusu artık veri düzeyinde cevaplanabilir. Ayrıntı için
[sayaç haritası §5c](collectors/vsphere-counter-map.md).

## 4. Sıkıştırma (compaction) — **aritmetik olarak tam**

Canlı veriye karşı, 602 284 ham örnek üzerinde:

| Kontrol | Karşılaştırılan | Uyuşmazlık |
|---|---|---|
| 5-dakika kovası = ham örnekler (min/max/sum/count) | 68 390 kova | **0** |
| Saatlik kova = içindeki 12 adet 5-dakikalık kova | 3 352 kova | **0** |
| Su işaretinin altında kalıp hiç katlanmamış seri | — | **0** |

Boyut: 53 MB ham örnek, 12 MB kova. Kova yolu bugüne kadar yalnızca birim
testlerle sınanmıştı; bu, gerçek veriye karşı ilk doğrulaması.

Tekrar etmek için `docs/collectors/` altındaki sorgular değil, doğrudan:

```sql
WITH raw AS (
  SELECT p.series_id, (p.at_utc / 300) * 300 AS bucket_start,
         min(p.value) mn, max(p.value) mx, sum(p.value) sm, count(*) ct
  FROM sample p GROUP BY 1,2)
SELECT count(*) compared,
       count(*) FILTER (WHERE abs(b.sum_value - r.sm) > 1e-6) AS sum_mismatch,
       count(*) FILTER (WHERE b.sample_count <> r.ct)         AS count_mismatch
FROM bucket b JOIN raw r ON r.series_id = b.series_id AND r.bucket_start = b.start_utc
WHERE b.resolution = 'FiveMinutes';
```

## 5. Sayfalama (paging)

`maxObjects` bir tavandır, emir değil. 250 istendiğinde vCenter 100 nesne + devam
jetonu döndürdü; 199 nesne 2 sayfada eksiksiz geldi. **Devam yolu canlıda
kanıtlandı.**

Probe bunu önce "SUSPECT" diye işaretliyordu; ölçüm yanlış olanın uyarı olduğunu
gösterdi ve uyarı kaldırıldı. Tehlikeli olan tersidir — erken durmak, küçük ve
sağlıklı bir ortam gibi görünür.

## 6. vCenter alarmları

Tek bir tetiklenmiş alarm (`Host memory status`, `host-3615`) hem host'ta hem
kümesinde, baytına kadar aynı şekilde geldi. `key` (`115.3615`) üzerinden
tekilleştirildi, `entity` alanına göre host'a atfedildi, adı ikinci bir çağrıyla
çözüldü, PostgreSQL'e `Critical / Open / confirmed` olarak yazıldı ve ekranda
Acknowledge / Silence / Clear ile göründü.

---

## 7. Disk büyümesi — ölçüldü, ve bir varsayımı çürüttü

103 saniyelik pencerede, 8 620 seri ile:

| | Ölçüm |
|---|---|
| Örnek yazma hızı | 335 / saniye |
| Veritabanı büyümesi | 20,9 KB/s = **1,73 GB/gün** (retention silmeden önceki ham hız) |
| Satır başına (`sample`) | 92,2 bayt (indeks dahil) |
| Satır başına (`bucket`) | 173,1 bayt |

### Kararlı durum projeksiyonu

Yapılandırılmış retention ile (2 gün ham / 30 gün 5-dakika / **90 gün** saatlik):

| Katman | Seri başına satır | Toplam satır | Boyut |
|---|---|---|---|
| Ham (2 gün) | 5 760 | 35,0 M | 3,0 GB |
| 5 dakika (30 gün) | 8 640 | 52,6 M | 8,5 GB |
| Saatlik (90 gün) | 2 160 | 13,1 M | 2,1 GB |
| **Toplam** | | | **≈ 13,6 GB** |

**Bu 200 varlıklı bir estate için.** ADR-0012'nin örnek ortamı daha büyük
(30 host, 800 VM).

> Bu tablo **6 084 seri** ve **90 günlük saatlik saklama** içindir. İki karar
> art arda alındı: `storagePath` gecikme çifti bırakıldı (8 620 → 6 084 seri,
> 29,6 → 20,9 GB), ardından saatlik saklama 400 → 90 güne indi (20,9 → 13,6 GB,
> [ADR-0017](adr/0017-retention-windows-set-by-measurement.md)). Toplam düşüş
> **%54**.

### Çürütülen varsayım

ADR-0012 şöyle diyor: *"Depolama maliyeti her adımda kabaca on kat düşüyor, bu
yüzden uzun kuyruk neredeyse bedava."*

**Birim zaman için doğruydu, yapılandırılmış retention için değil.** 10 kat
azalma, 15 kat ve 200 kat daha uzun saklama süresiyle fazlasıyla telafi
ediliyordu:

```
Ham         2 880/gün ×   2 gün =  5 760 satır/seri
5 dakika      288/gün ×  30 gün =  8 640 satır/seri   ← ham'dan FAZLA
Saatlik        24/gün × 400 gün =  9 600 satır/seri   ← en büyüğü
```

Her katman kendinden öncekinden **daha çok** satır tutuyordu; "diskin neredeyse
tamamı ham penceresi" ifadesi tam tersine dönmüştü.

**Bu ölçüm bir karara yol açtı.** Saatlik kademe 90 güne indirildi
([ADR-0017](adr/0017-retention-windows-set-by-measurement.md)) ve aritmetik
ilk kez iddiayı destekliyor:

```
Ham         2 880/gün ×   2 gün =  5 760 satır/seri
5 dakika      288/gün ×  30 gün =  8 640 satır/seri   ← artık en büyüğü
Saatlik        24/gün ×  90 gün =  2 160 satır/seri   ← artık en küçüğü
```

Bedeli: **yıla yıl karşılaştırma bitti.** ADR-0012 400 günü tam olarak bunun
için seçmişti. Mevsimsellik ("her Aralık toplu iş ikiye katlanıyor") artık
ürünün verisinden görülemez; ADR-0017 bunu ve geri alma yollarını kaydediyor.

### Verilen karar: yol gecikmesi bırakıldı

İlk ölçümde 8 620 serinin 5 072'si (%59) depolama yolu serisiydi ve kararlı
durum ≈ 29,6 GB çıkıyordu. Seçenekler ölçülüp sunuldu; **karar: gecikme çifti
bırakılsın, hata sayaçları kalsın.**

| | Seri | Kararlı durum |
|---|---|---|
| Ölçüldüğü hâli (4 yol sayacı) | 8 620 | 29,6 GB |
| **Bugünkü hâli (2 yol hata sayacı)** | **6 084** | **20,9 GB** |
| `storagePath` tamamen çıkarılsaydı | 3 548 | 12,2 GB |

Gerekçe tek cümleyle: gecikme çifti 2 536 seri ve 8,7 GB'a mal oluyordu,
karşılığında "hangi yol yavaş" sorusunu cevaplıyordu — ama §5b bu estate'te yol
gecikmesinin 1 ms altında zaten çözülemediğini ölçmüştü. Hata sayaçları "hangi
yol **bozuk**" diyor ve onların sıfırı gerçek bir cevap.

**Bedeli kaydedildi:** kalan iki sayaç yolu çalışma zamanı adıyla
(`vmhba0:C0:T0:L1`) adlandırıyor, LUN kimliği taşımıyor. Artık bir bus reset
host ve HBA'ya atfedilebiliyor, otomatik olarak datastore'a değil. Datastore'a
kadar bağlamak gerekirse yol belli: host'un `config.storageDevice.multipathInfo`
haritası, envanter ritminde bir kez okunur — seri ödemeden.

### Bir sayaç bırakıldığında geçmişine ne oluyor

Canlıda doğrulandı, çünkü aşikâr değil:

```
storagePath.busResets.summation        1268 seri — son 2 dk'da 1268'i yazıldı
storagePath.commandsAborted.summation  1268 seri — son 2 dk'da 1268'i yazıldı
storagePath.totalReadLatency.average   1268 seri — son 2 dk'da 0
storagePath.totalWriteLatency.average  1268 seri — son 2 dk'da 0

döngü başına yazılan seri: 6 084   (önce 8 620, −%29)
toplam seri satırı:        8 620
```

Bırakılan 2 536 seri **silinmedi**. Yazılmayı durdurdular ama topladıkları
geçmiş gerçek ölçümdü ve duruyor: ham pencere 2 gün, kovalar 30 ve **90** gün
sonra retention ile temizlenecek. (ADR-0017 öncesi bu 400 gündü.)

Ölçüm sırasında bu paragraf, ölçümlerin gitmesinden sonra seri satırlarının da
kaldırılacağını söylüyordu. **Artık kaldırılmıyor:** o süpürme adımı
ADR-0019 ile kaldırıldı. Yani `series` tablosu 8 620'de **kalıcı olarak**
duracak — 2 536 boş satır, satır başına ~300 bayt disk ve ~370 bayt bellek,
toplam **≈ 1,7 MB**, bir kere ve temelli. Tek bir yapılandırma değişikliğinin
bıraktığı bu miktar, yıllık makine değişiminin bıraktığından (yılda ~600-1 200
satır) büyüktür; ikisi de 150-170 MB'lık çalışma kümesinin yanında görünmez.

Bu doğru davranış: toplamayı bıraktık diye toplanmış ölçümü silmek, veriyi yok
etmek olurdu. Ama projeksiyon tablosu **yeni yazım hızı** içindir; disk birkaç
gün boyunca eski geçmişi de taşıyacak.

**Karar verildi:** saatlik saklama 90 gün. 13,6 GB. Ayrıntı ve kaybedilen
yıla yıl karşılaştırma için
[ADR-0017](adr/0017-retention-windows-set-by-measurement.md).

Sıradaki kısılacak yer artık saatlik değil: **beş dakikalık kademe** en büyüğü
(8 640 satır/seri, 8,5 GB). 13,6 GB bile fazla gelirse orası bakılacak yerdir.

---

## 8. vCenter gerçek zamanlı slotu değerlerinden önce listeliyor

**Ölçüldü, 22 Eylül 2026.** vCenter yeni bir 20 sn'lik slotu `sampleInfo`'da,
o slotun değerleri henüz dolmadan (`-1`) listeliyor; birkaç saniye sonraki bir
okuma aynı slotu dolu getiriyor. 07:21:20Z'de beş host'ta 759 ham serinin
**474'ü ilk görüşte `-1`** idi (datastore 180, disk 160, cpu 96, net 24,
mem toplamı 8, net toplamı 4, …); iki host'ta yalnızca datastore instance'ları.
07:13:40 ve 07:14:20'de de aynısı görüldü. **Hiçbir slot kalıcı olarak `-1`
kalmadı** — hepsi sonraki okumada doldu.

**Nasıl ortaya çıktı:** H3 (#73, canlı okuma işaretten başlar, `StartTime`
dışlayıcı) yayımlandıktan sonra tek bir host her `:00` slotunda tüm serilerini
kaybetti (~540 örnek/dk, satırların ~%2'si). Ayrıştırıcı `-1`'i atıp slotu yine
"en yeni zaman" diye bildiriyordu; işaret onun ötesine geçti, sonraki pencere
bir daha sormadı. **Eski kod her turda son 3 örneği yeniden okuyup
`ON CONFLICT DO NOTHING`'e bırakıyordu — bu davranışı iki gün boyunca yanlışlıkla
doğru biçimde gizledi.** Düzeltme #76: dolmamış slotlar host başına tutulur ve
en çok 6 örnek geriye kadar yeniden okunur; dolu değer bir kez yazılır; kısmi
birleşik (toplam/en büyük) değer yazılmaz; 6 örnekte dolmayan slot "düşürülen
örnek" olarak sayılır.

**Nasıl tekrar edilir:** `dotnet run --project tools/EnterpriseObservatory.VsphereProbe
-- --from-store --mask --late-samples --follow` (salt-okunur; `EO_PG_PASSWORD`
gerekli). Yayım sonrası kontrol: 20 sn adımı başına `sample` sayısı (bu estate'te
8.828) ve dakika başına satır (~26.660).

**Ders:** "önce ölç" kuralının bir tuzağı — tek turluk anlık görüntü, zamanla
değişen bir sunucu davranışını kaçırır. Bu davranışı ancak aynı sorguyu
saniyeler arayla tekrarlayan `--follow` yakaladı.

## 9. Dört saatlik kesinti: boşluk kaydı ve sessiz kaynağın alarmları

**Ölçüldü, 22 Eylül 2026.** Makine kurumsal ağdan düştü; vCenter'ın adı
(`ebebek-cls-vcenter.ebebek.local`) DNS'te çözülmedi. Toplama **07:58Z → 12:00Z**
arası durdu (servis ayaktaydı, kaynak cevap vermiyordu). Ad döndükten sonra devre
kesici 5 dakikalık beklemesini bitirip kendiliğinden toparlandı: envanter
12:00:59Z Healthy, gözlem 12:03:26Z, ardışık hata 0.

**H3'ün ilk gerçek testi — geçti.** `collection_gap` satır 1:

| Alan | Değer |
|---|---|
| `gap_from` → `gap_to` | 07:58:00 → 11:59:24 |
| `lost_before` | 11:02:24 (= yeniden bağlanma − 57 dk; host'un ~1 sa gerçek zamanlı saklaması) |
| `filled_to` | 11:59:24 |
| `state` | `unrecoverable` |
| açıldı / kapandı | 12:01:24 / 12:02:10 |

57 dakikalık kurtarılabilir pencere **46 saniyede** doldu: 11:02–11:59 arası her
dakika ~26.484 satır (8.828 seri × 3), eksiksiz. 07:58–11:02 kurtarılamaz ve
kayıt bunu açıkça söylüyor — sessizce silinmedi.

**#63 tuttu.** Kesinti süresince hiçbir alarm çözülmedi. 29 `ConditionCleared`'ın
tamamı 12:01–12:02Z'de, vCenter döndükten **sonra** ve hepsi
`storage-latency-blind-spot`: yeniden bağlanmanın ilk turunda yük düşük, kural
"ölçüm çalışıyor mu" diyemedi ve bugünkü kod çözdü; birkaçı aynı dakikada geri
döndü. Z1'in 399 çalkalanmasıyla aynı desen — ADR-0026'nın "sakin tur =
NotJudgeable = Unknown" kuralı bunu kapatır.

**Bulunan açık:** host.log iki kez `[Raised] Warning Collector unreachable
(metrics/inventory)` yazdı, ama `alert_instance`'ta böyle bir satır ve
`alert_transition`'da geçiş **yok** — dört saat boyunca "toplayıcıya ulaşılamıyor"
alarmı operatörün ekranında görünmedi, yalnızca logda. Düzeltme sırada (K2'den önce).

**#76'nın canlı doğrulaması (aynı gün, yayım 12:05:32Z, ~4 sn kesinti):** yayımdan
sonraki her `:00` slotu **8.828** örnek (12:06–12:11), dakika başına satır
**26.663**'e döndü, yeni boşluk satırı açılmadı (kesinti canlı okumanın erişimi
içinde). İstisnalar: 12:05:20 slotu 8.469 — yeniden başlatma anında eski kodun
son okuması (hata henüz düzeltilmemişti); 12:07:40 slotu 8.826 (2 eksik).

**Nasıl tekrar edilir:** `collection_gap` satırı ve dakika başına `sample` sayısı
(bkz. §8'in sorguları); alarmlar için `alert_transition`'da kesinti penceresinde
`reason` dağılımı.

## 10. Yedek tazeliği (M8.8): özel nitelik okuması ve RPO

**Ölçüldü, 22 Eylül 2026.** Ayrıntı: `docs/measurements/backup-freshness-shapes.md`.
Yalnızca sayı; değer ve ad basılmadı.

- **Okuma, tam envanter isteğinde:** `customValue` 145/145 VM, hata 0; yedek
  aracının son yedek niteliği 86 VM'de, 86'sı da zamana çevrildi; 59 VM'de
  hiç özel değer yok.
- **Olaylardan doğrulama** (`source_event`, ~24 saatlik pencere): son yedek
  niteliği 70 kez / 47 VM'de değişti, VM başına günde 1,47. Değer ile olay
  zamanı farkı **70/70'te −3 saat** — değerler UTC+03:00 yerel saat,
  toplayıcının saat dilimi varsayımı doğru.
- **RPO'yu ölçüm belirledi:** ardışık iki yedek arası 23 aralığın 10'u 24–30
  saat, hiçbiri 30 saatin üstünde değil. Sınır 36 saat (günlük + 12 sa).
- **Beklenen yargılar (son okuma):** 81 Passing, 5 Failing (beşi de 30 günden
  eski), 59 NotEvaluated ("no backup attribute").

**Nasıl tekrar edilir:** `probe --from-store --candidates-backup` (sonundaki
"collector's reading" bölümü); olay tarafı için ölçüm belgesindeki (d)
sorguları 1–3.

## 11. Kör-nokta histerezisi (K/P): pencere eşiği canlı veriden

Kural: `storage-latency-blind-spot`. Ham örnekler üzerinden yeniden oynatma
(`docs/measurements/blind-spot-hysteresis-replay.sql`, salt-okunur), 22 Eylül 2026,
2,00 gün, 29 volume, 90.624 yargılanan tur. **Ölçülebilirlik %4,5** — bu estate
neredeyse tamamen kör; çalkalanma o %4,5'lik ölçülebilir turlardan geliyor.

| variant | present % | verdict flip/gün | alarm flip/gün | ≥1 flip/gün olan volume | pencere dolarken % |
|---|---|---|---|---|---|
| current (K=1) | 95,5 | 2916,0 | 369,9 | 20 | 0,0 |
| K=10 P=50 | 97,2 | 115,0 | 95,0 | 7 | 0,3 |
| K=10 P=70 | 99,2 | 64,0 | 44,0 | 4 | 0,3 |
| K=20 P=50 | 97,4 | 62,5 | 49,5 | 4 | 0,6 |
| K=20 P=70 | 99,4 | 25,0 | 21,0 | 4 | 0,6 |
| K=30 P=50 | 97,5 | 50,0 | 38,0 | 3 | 0,9 |
| K=30 P=70 | 99,4 | 19,5 | 15,0 | 2 | 0,9 |
| **K=30 P=90 (seçilen)** | **99,9** | **6,0** | **5,0** | **1** | **0,9** |
| K=60 P=70 | 100,0 | 0,0 | 0,0 | 0 | 1,9 |
| K=60 P=90 | 100,0 | 0,0 | 0,0 | 0 | 1,9 |

**Gerçek körlüğü yakalama** (≥30 dakikalık, ölçülebilir turu olmayan 385 kesintisiz
dilim; §4 blind-summary): K=30 P=90 **385'in %100'ünü** yakalıyor, en kötü durumda
30 yargılanan tur = **29,0 dakika**, ortanca 0,0 dakika (dilimlerin çoğu zaten
Present başlıyor). 3 dilim hiç Present'e ulaşmıyor — pencereyi dolduramadan bitiyorlar.

K=60 bu estate'te sıfır çalkalanma veriyor, ama bu %4,5 ölçülebilirliğin artefaktı:
ölçüm geri geldiğinde fark etme süresini iki katına çıkarır. Seçim K=30 P=90.

**Ders (ölçüm sorgusunun kendi varsayımı):** §4/§5 ilk koşuda boş döndü. Sebep
`HAVING count(*) >= 60` idi — "30 dakika" yerine "60 satır" sayıyor ve dakikada iki
yargılanan tur varsayıyordu. Gerçek hız volume başına ~1,1/dk olduğu için eşik neredeyse
iki katı süre istedi ve bir saatten kısa her gerçek kör dilimi düşürdü. Duvar saatine
çevrildi (`>= 1800 sn`). Kural: **satır sayısı geçen süre değildir**; "önce ölç"
kuralına ek olarak ölçüm sorgusunun varsayımı da sınanır. Aynı sebeple: seçilen
varyant (K=30 P=90) yeniden oynatma SQL'inde ve `BlindSpotReplayTests`'te bulunmalı —
yoksa yayımlanan ayar, hiç ölçülmemiş tek ayar olur.

Yeniden oynatma SQL'i canlı veritabanında koşulmadan önce compaction'ın
`completed_to_utc`'sine bakılır: ikisi aynı ham `sample` tablosunu okuyor ve ağır
okuma compaction'ı zaman aşımına düşürebiliyor (22 Eylül'de 4 kez oldu).
Yayım sonrası 24 saatte çalkalanma yeniden sayılacak (hedef: volume başına ≤1/gün
ortalama; 1 volume'ün çalkalanmaya devam etmesi kabul edilebilir).
## 12. Migrasyon 15 (F2): çift sütun bildirimi ve kapının yakalaması

22 Eylül 2026. F2'nin `collector_health.skipped_cycles` sütunu **iki yerde** bildirilmişti:
tablonun `CREATE TABLE`'ında ve migrasyon 15'in `ALTER TABLE`'ında. Taze veritabanında
create sütunu yaratıyor, ardından migrasyon `42701: column "skipped_cycles" already exists`
ile düşüyordu — 132 PostgreSQL testi kırmızı. **Her yeni kurulumda ürün açılmazdı.**

Kural (zaten kodda, yazıya geçiyor): bir migrasyonun eklediği sütun temel `CREATE TABLE`'da
TEKRARLANMAZ — `alert_instance` da `stale_reason`/`consecutive_absent`'i aynı şekilde
listelemez. Ek olarak: geri sarma yardımcısı (`RewindTo13`) yeni sütunu düşürmeli, yoksa
migrasyon tekrar oynatılamaz.

**Ajan bunu göremezdi:** kendi ortamında `EO_TEST_PG_PASSWORD` yok, 134 test atlandı ve
raporunda bunu açıkça yazdı. **Şema değiştiren her PR'da tek kanıt yerel kapıdır, ajan
raporu değil.** Literal şema sabiti (`Assert.Equal(15, Version())`, `PostgresSchema.Current`
DEĞİL) bu yüzden var: migrasyon eklemek birinin elle onaylaması gereken bir olay olsun diye.

Yayım: pg_dump önce alındı (182 MB), canlıda `schema_version` 14 → 15 sorunsuz uygulandı,
`skipped_cycles` üç rol için de 0.
## Bilinen sınırlar — ölçülmüş, tahmin edilmemiş

### Gecikme 1 ms altında görünmüyor

Kısa bir pencerede (302 örnek) **hiçbir** datastore gecikme sayacı 0'dan büyük
okumadı; aynı hacimler 3 762 okuma + 8 230 yazma IOPS yapıyordu. Uzun pencerede
tablo değişiyor — aşağıdaki düzeltmeye bakın.

| Sayaç | Sıfırdan büyük | Neden |
|---|---|---|
| `datastore.total{Read,Write}Latency` | 0 / 302 | Tam milisaniye raporlanıyor; all-flash dizide 0'a kesiliyor |
| `datastore.datastoreVMObservedLatency` | 0 / 302 | Yalnızca SIOC etkinken raporlar |
| `datastore.siocActiveTimePercentage` | **0 / 302** | SIOC bu estate'te hiçbir yerde etkin değil |

**Sıfır, sessizlikten beterdir: sıfır ölçüm gibi görünür.** Sayaçlar
toplanmaya devam ediyor (1 ms üstü — yani asıl aranan sorun — doğru görünür) ve
SIOC kanıtı veritabanında duruyor. Uygunluk motoru yazıldığında ilk kurallardan
biri bu olmalı.

### Düzeltme (uzun pencere): "her zaman 0" fazla kesin bir ifadeydi

Yukarıdaki tablo **302 örneklik anlık bir pencereden** alınmıştı. 178 180
okumaya çıkıldığında tablo değişiyor:

| Sayaç | Sıfırdan büyük | Oran | Maks |
|---|---|---|---|
| `datastore.totalReadLatency.average` | 669 / 178 180 | %0,38 | **7 ms** |
| `datastore.totalWriteLatency.average` | 254 / 178 180 | %0,14 | **8 ms** |
| `datastore.datastoreVMObservedLatency.latest` | 0 | %0 | 0 (SIOC kapalı) |

Yani **sonuç doğruydu, ifade değildi.** Sayaçlar "hep 0" okumuyor; okumaların
%99,6'sı 0 çünkü 1 ms altı kesiliyor, ama **1 ms'yi aşan sivrilmeler
görünüyor** — 7-8 ms'ye kadar. Bu tam olarak §5b'nin sayaçları tutma
gerekçesiydi: *"1 ms üstü — yani asıl aranan sorun — doğru görünür."*

Ders, ölçümün kendisi kadar önemli: **kısa bir pencereden mutlak bir cümle
kurmak.** "0 / 302" doğru bir gözlemdi; "her zaman 0" ondan çıkarılan yanlış
bir genellemeydi.

### Bırakılan yol gecikmesi ne gösteriyormuş

Dürüstlük gereği, aynı uzun pencerede:

| Sayaç | Sıfırdan büyük | Oran | Maks | ≥10 ms görülen yol |
|---|---|---|---|---|
| `storagePath.totalReadLatency.average` | 1 835 / 578 680 | %0,32 | **48 ms** | 3 |
| `storagePath.totalWriteLatency.average` | 834 / 578 680 | %0,14 | 28 ms | — |

**Sinyal vardı.** 48 ms'lik bir yol gecikmesi önemsiz değil. Ama seyrek ve
yoğunlaşmış: 578 680 okumanın 3 ayrı yolunda 10 ms'yi aşıyor, 20 ms'yi aşan
tek bir okuma var.

Karar (yol gecikmesini bırakmak, hata sayaçlarını tutmak) geri alınmadı —
maliyeti 2 536 seri ve 8,7 GB'dı ve karar kullanıcınındı. Ama **bu sayı
kayda geçiyor**, çünkü kararı yeniden değerlendirmek gerekirse dayanak bu
olmalı, hatıra değil.

Buna karşılık `storagePath.busResets` / `commandsAborted` sıfırları **bilgidir**:
7 608 örnekte tek bir bus reset yok. Fark sayacın türünde — kesilen bir ortalama
hiçbir şey söylemez, toplanan bir sayaç "hiç olmadı" der.

### Ölçüm olmayan sayılar saklanıyordu — düzeltildi

R2 yazılırken host başına ortalama gecikmeler **negatif** çıktı. Gecikme
negatif olamaz.

Canlı veriye bakıldığında, topladığımız **her sayaçta** `-1` bulundu:

| Sayaç ailesi | Negatif oran |
|---|---|
| `virtualDisk.total{Read,Write}Latency` | %0,52 |
| `cpu.ready` / `cpu.costop` | %0,33 |
| `datastore.*` | %0,22 |
| `disk.*` | %0,15 |

Ayrıca `disk.kernelLatency` içinde **−1,8446744073709553e+18** (≈ −2⁶⁴/10) —
hangi yorumla bakılırsa bakılsın çöp bir değer.

`-1`'in vim25'te ne anlama geldiği **ulaşılabilen belgelerde yazmıyor**, ve
gerekmiyor: ne anlama gelirse gelsin, bir milisaniye sayısı değil. Şu ikisi
bunu doğruluyor — yüzde sayaçlarında `-0.01` (yani `-1` ÷ 100) ve mikrosaniye
sayacında `-0.001` (yani `-1` ÷ 1000) görülüyor; yani ham değer tam olarak
`-1` idi ve bizim normalleştirmemizden geçmiş.

**Zararı:** host ortalamalarını sıfırın altına çekiyordu; tek bir −1,8e18
değeri `disk.kernelLatency` grafiğinin eksenini öyle ölçekler ki gerçek
değerlerin hepsi tek bir düz çizgiye oturur.

**Düzeltme:** negatif okuma **düşürülüyor**, sıfıra kırpılmıyor. Sıfır bir
ölçümdür; bu ise ölçümün yokluğudur — ve ürün bunu zaten modelliyor: *boşluk
boşluk kalır, asla sıfırla doldurulmaz.* Grafikte boşluk görünür, uydurulmuş
bir sıfır görünmez.

Canlı doğrulama: yeniden başlatmadan sonraki **30 420 örnekte 0 negatif**.
Eski 8 021 tanesi geçmiş olarak duruyor; retention temizleyecek.

*Not:* bir gün BMC sıcaklığı gibi **gerçekten işaretli** bir sayaç toplanırsa
bu kural sayacın meta verisine taşınmalı; bugün topladığımız hiçbir sayaç
(gecikme, IOPS, yüzde, bellek, sayım) negatif olamaz.

### Yol sayaçları iki farklı sözlük kullanıyor

Hata sayaçları `vmhba0:C0:T0:L1`, gecikme sayaçları
`fc.<WWNN>:<WWPN>-fc.<WWNN>:<WWPN>-naa.<LUN>`. Aynı yolu adlandırıyorlar ve
**doğrudan birbirlerine eklenemiyorlar** — "bu yolda reset oldu *ve* bu yol
yavaş" henüz tek sorguda sorulamıyor.

### Sağlık yayılımı yok — cluster'lar kalıcı olarak Unknown

Kod incelemesinde bulundu, ekranda görünmüyordu çünkü `Unknown` meşru bir
durum gibi okunuyor.

ADR-0004 hangi kenarların sağlık yayacağını ve **yönünü** karara bağlamış
(containment: çocuk → ebeveyn, hosting: sağlayıcı → tüketici; `SameAs` ve
`ConnectedTo` yaymaz). `RelationshipRules.PropagatesHealth` bunu kodluyor ve
**testleri var**. Ama üretim kodunda **tek bir çağıranı yok.**

Sonuç: 3 cluster'ın üçü de ilk döngüden beri `Unknown`. Bir cluster'ın içinde
`Critical` bir host varken cluster `Unknown` görünüyor. Genel bakıştaki
"Unknown · 10" sayısının 3'ü bu.

**Hata değil, eksik karar.** ADR-0004 yönü söylüyor ama **birleştirme
fonksiyonunu** söylemiyor: on host'tan biri kritikken cluster kritik mi,
düşmüş (degraded) mü, yoksa HA görevini yaparken hiçbir şey mi? Bu bir ürün
kararı.

`Unknown` bırakıldı çünkü hesaplanmamış bir sorunun dürüst cevabı odur —
ürünün 1. ilkesi tespit etmediğini iddia etmemek. Yanıltıcı olan yorum
satırıydı ve düzeltildi.

**Karar gerekiyor:** birleştirme fonksiyonu ne olmalı?

### Tanımlanmış, test edilmiş, çağrılmamış

Cluster sağlığı bulgusundan sonra aynı desen sistematik olarak arandı:
**src'de yalnızca tanımında geçen ama testlerde kullanılan üyeler.** Beş aday
çıktı, hepsi elle doğrulandı:

| Üye | Ne yapar | Durum |
|---|---|---|
| `RelationshipRules.PropagatesHealth` | Sağlık hangi kenarda yayılır | Çağıranı yok → cluster'lar Unknown (yukarıda) |
| `CounterValue.AsPercentageOfInterval` | `cpu.ready` ms → % | **Çağıranı yok** |
| `RelationshipRules.MayContainCycles` | `ConnectedTo` döngüye izin verir | Çağıranı yok — henüz `ConnectedTo` kenarı da yok |
| `RelationshipRules.IsSymmetric` | `SameAs`/`ConnectedTo` çift yönlü | Çağıranı yok — aynı sebep |
| `VsphereSoapFault.IsSurvivable` | Hangi hata hayatta kalınabilir | **Kural iki yerde yazılmıştı** — düzeltildi |

Son ikisi hakkında:

**`IsSurvivable` düzeltildi.** Aynı kural bir kez `VsphereSoapFault` üzerinde
adlandırılmış, bir kez de `VsphereClient` içinde `catch ... when` filtresi
olarak elle yazılmıştı. İki farklı tip üzerinde olduğu için birbirini
çağıramıyorlardı. Artık `VsphereFaults.IsSurvivable(kind)` tek tanım ve ikisi
de onu kullanıyor. Bir kuralın iki yazılışı, sürüklenmesi için iki şanstır.

**`AsPercentageOfInterval` bilerek bağlanmadı.** `cpu.ready.summation`, bir
VM'in CPU çekişmesini gösteren tek güvenilir sayaç ve **toplam milisaniye**
olarak saklanıyor. Kendi belgesi "toplandığı aralığa bölünmeden anlamsız"
diyor; dönüşüm yazılmış, 7 testi var, hiçbir yerden çağrılmıyor.

Bugün ekranda `cpu.ready.summation · 522 · millisecond · as sampled` görünüyor.
Bu **yanlış değil** — birim yazıyor, "as sampled" yazıyor. Ama:

- Sektördeki eşik **yüzde** cinsinden ifade edilir (>%5 sorun, >%10 ciddi).
  522 ms'yi operatör kafadan çeviremez.
- 20 saniyelik aralıkta 522 ms = %2,6; 300 saniyelik aralıkta aynı sayı
  %0,17. **Farklı aralıklardaki iki VM'i ham milisaniyeyle karşılaştırmak
  geçersizdir.** Bugün her şey 20 sn olduğu için ısırmıyor.

Bağlanmadı çünkü bu bir **sunum kararı**: yüzde mi gösterilsin, ikisi birden
mi, yoksa ham değer kalıp eşik motoru mu çevirsin? Üçü de savunulabilir.
**Karar gerekiyor.**

`MayContainCycles` ve `IsSymmetric` ileriye dönük: `ConnectedTo` kenarı üreten
bir toplayıcı (SAN switch) henüz yok. Yine de bugün hiçbir döngü doğrulaması
çalışmıyor — o toplayıcı geldiğinde bu iki kuralın bağlanması gerektiği not
edilmeli.

### psql üzerinden ölçüm bu makinede yanıltıcı

Sunucuda 0.24 ms'de biten sorgu psql duvar saatinde 6–53 saniye görünüyor.
Fark tamamen loopback gidiş-dönüşünde. Servisin kendi yolu etkilenmiyor
(API 10–180 ms). Bu makinede performans ölçerken `EXPLAIN (ANALYZE)`
kullanın, istemci saatini değil.

---

## Henüz doğrulanmamış

Dürüstlük gereği: aşağıdakiler **çalışıyor diye bilinmiyor.**

- **Uzun süreli çalışma — kısmen ölçüldü.** En uzun kesintisiz koşu **3 saat**
  (her derleme servisi durdurmayı gerektirdiği için daha uzunu zor). O koşuda
  çalışma kümesi **149 MB → 172 MB**: saatte ~8 MB, düz değil ama kaçak da
  değil; .NET'te sunucu GC'nin bu ölçekte beklenen davranışına benziyor. Tek
  uyarı, tek hata yok. **Günler boyunca** ölçülmedi ve asıl merak edilen o:
  8 MB/saat sürerse mi duruyor, yoksa yassılaşıyor mu? (Disk büyümesi §7'de
  ayrıca ölçülü.)
- **Retention silmeleri canlıda.** Silme yolu *test edilmemiş değil*: gerçek bir
  PostgreSQL'e karşı üç entegrasyon testi var (`Raw_samples_are_folded_before_
  they_are_deleted`, `Everything_past_its_retention_goes`,
  `A_series_keeps_its_row_after_its_last_measurement_has_aged_out`) ve saat
  ileri sarılarak 2 gün / 30 gün / 90 gün sınırlarının üçünü de geçiyor. Doğrulanmamış olan, **canlı
  estate'te tetiklenmesi** — veri henüz o kadar eski değil, dolayısıyla
  gerçek hacimde silmenin ne kadar sürdüğü ve dosyayı nasıl etkilediği
  bilinmiyor.
- **Şema göçü canlı bir yükseltmede.** Mekanizma artık gerçek bir PostgreSQL'e
  karşı test ediliyor (`SchemaTests`, 5 test): boş veritabanı sürüm 2'ye
  ulaşıyor ve 19+ tablo oluşuyor; zaten güncel bir kurulumu açmak hiçbir şeyi
  değiştirmiyor ve veriyi koruyor; **daha yeni bir build'in yazdığı veritabanı
  reddediliyor** (eski binary'nin yeni şemaya yazması, çökmeden ve yavaşça
  bozan türden bir hatadır); yarıda kalan bir göç PostgreSQL'in işlemsel
  DDL'i sayesinde arkasında hiçbir şey bırakmıyor; `schema_version` tek satır
  tutuyor. Doğrulanmamış olan, **gerçek veri üzerinde gerçek bir sürüm
  yükseltmesi** — bugün yalnızca iki göç var ve ikisi de boş veritabanında
  çalıştı. Göç aracı hâlâ yok (ADR-0016'da kabul edilmiş borç).
- **İkinci bir vCenter.** Kimlik katlama (ADR-0003) tek kaynakla sınanıyor;
  `relationship_evidence` bu yüzden 0 satır.
- **Yetkisi kısıtlı hesap.** Bağlantı `gentel@vsphere.local` ile kuruldu.
  Salt-okunur bir servis hesabının hangi özellikleri okuyamadığı ölçülmedi —
  `missingSet` yolu kodda var ve test edildi, canlıda tetiklenmedi.



