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

Yapılandırılmış retention ile (2 gün ham / 30 gün 5-dakika / 400 gün saatlik):

| Katman | Seri başına satır | Toplam satır | Boyut |
|---|---|---|---|
| Ham | 5 760 | 49,7 M | 4,3 GB |
| 5 dakika | 8 640 | 74,5 M | 12,0 GB |
| Saatlik | 9 600 | 82,8 M | 13,3 GB |
| **Toplam** | | | **≈ 29,6 GB** |

**Bu 200 varlıklı bir estate için.** ADR-0012'nin örnek ortamı daha büyük
(30 host, 800 VM).

### Çürütülen varsayım

ADR-0012 şöyle diyor: *"Depolama maliyeti her adımda kabaca on kat düşüyor, bu
yüzden uzun kuyruk neredeyse bedava."*

**Birim zaman için doğru, yapılandırılmış retention için değil.** 10 kat
azalma, 15 kat ve 200 kat daha uzun saklama süresiyle fazlasıyla telafi
ediliyor:

```
Ham         2 880/gün ×   2 gün =  5 760 satır/seri
5 dakika      288/gün ×  30 gün =  8 640 satır/seri   ← ham'dan FAZLA
Saatlik        24/gün × 400 gün =  9 600 satır/seri   ← daha da fazla
```

Yani her katman, kendinden öncekinden **daha çok** satır tutuyor. Uzun kuyruk
bedava değil; en pahalı kısım o.

Bu bir hata değil — yapılandırma tam olarak amaçlandığı gibi çalışıyor. Ama
gerekçe sayılarla uyuşmuyor ve bu, sayılar ölçülene kadar görülemezdi.
ADR'ler düzenlenmez; bu bulgu yeni bir ADR'yi hak ediyor.

### Ana sürücü: `storagePath`

8 620 serinin **5 072'si (%59)** depolama yolu serisi — 20 Eylül'de eklendi.
Payı:

| Seçenek | Kararlı durum |
|---|---|
| Bugünkü hâli | 29,6 GB |
| `storagePath` olmadan | 12,2 GB |
| Saatlik retention 400 → 90 gün | 19,3 GB |
| Saatlik retention 400 → 180 gün | 22,3 GB |

**Karar gerekiyor** (bkz. [analiz katmanı önerisi](proposals/analysis-layer.md)
§8'e ek olarak):

1. 30 GB, 200 varlıklı bir kurulum için kabul edilebilir mi? Kabul edilebilirse
   kurulum şartlarında **yazılmalı**, sürpriz olmamalı.
2. Yol gecikmesi (seviye 3, 2 sayaç) kesilip yalnızca yol *hataları* (seviye 2)
   tutulsa, seri sayısı yarıya iner. Hata sayaçları zaten "kablo/SFP bozuk"
   sinyalini veriyor; gecikme "hangi yol yavaş" sorusu için gerekli.
3. Saatlik retention gerçekten 400 gün mü olmalı? Bir yıllık kapasite eğilimi
   için evet; olay incelemesi için 2 günlük ham pencere zaten yeterli.

---

## Bilinen sınırlar — ölçülmüş, tahmin edilmemiş

### Gecikme 1 ms altında görünmüyor

Bu estate'te **her** datastore gecikme sayacı 0 okuyor; aynı hacimler 3 762
okuma + 8 230 yazma IOPS yapıyor.

| Sayaç | Sıfırdan büyük | Neden |
|---|---|---|
| `datastore.total{Read,Write}Latency` | 0 / 302 | Tam milisaniye raporlanıyor; all-flash dizide 0'a kesiliyor |
| `datastore.datastoreVMObservedLatency` | 0 / 302 | Yalnızca SIOC etkinken raporlar |
| `datastore.siocActiveTimePercentage` | **0 / 302** | SIOC bu estate'te hiçbir yerde etkin değil |

**Sıfır, sessizlikten beterdir: sıfır ölçüm gibi görünür.** Sayaçlar
toplanmaya devam ediyor (1 ms üstü — yani asıl aranan sorun — doğru görünür) ve
SIOC kanıtı veritabanında duruyor. Uygunluk motoru yazıldığında ilk kurallardan
biri bu olmalı.

Buna karşılık `storagePath.busResets` / `commandsAborted` sıfırları **bilgidir**:
7 608 örnekte tek bir bus reset yok. Fark sayacın türünde — kesilen bir ortalama
hiçbir şey söylemez, toplanan bir sayaç "hiç olmadı" der.

### Yol sayaçları iki farklı sözlük kullanıyor

Hata sayaçları `vmhba0:C0:T0:L1`, gecikme sayaçları
`fc.<WWNN>:<WWPN>-fc.<WWNN>:<WWPN>-naa.<LUN>`. Aynı yolu adlandırıyorlar ve
**doğrudan birbirlerine eklenemiyorlar** — "bu yolda reset oldu *ve* bu yol
yavaş" henüz tek sorguda sorulamıyor.

### psql üzerinden ölçüm bu makinede yanıltıcı

Sunucuda 0.24 ms'de biten sorgu psql duvar saatinde 6–53 saniye görünüyor.
Fark tamamen loopback gidiş-dönüşünde. Servisin kendi yolu etkilenmiyor
(API 10–180 ms). Bu makinede performans ölçerken `EXPLAIN (ANALYZE)`
kullanın, istemci saatini değil.

---

## Henüz doğrulanmamış

Dürüstlük gereği: aşağıdakiler **çalışıyor diye bilinmiyor.**

- **Uzun süreli çalışma.** En uzun kesintisiz koşu 20 dakika — çünkü her
  derleme, çalışan servisin DLL kilitleri yüzünden onu durdurmayı gerektiriyor.
  20 dakikada: 154 MB çalışma kümesi, 38 iş parçacığı, 782 tanıtıcı, 20 döngü,
  tek uyarı yok. Bellek büyümesi ve bağlantı havuzu davranışı **saatler
  boyunca** ölçülmedi. (Disk büyümesi artık §7'de ölçülü.)
- **Retention silmeleri canlıda.** Silme yolu *test edilmemiş değil*: gerçek bir
  PostgreSQL'e karşı üç entegrasyon testi var (`Raw_samples_are_folded_before_
  they_are_deleted`, `Everything_past_its_retention_goes`,
  `A_series_with_nothing_left_is_forgotten`) ve saat ileri sarılarak 2 gün /
  30 gün / 400 gün sınırlarının üçünü de geçiyor. Doğrulanmamış olan, **canlı
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
