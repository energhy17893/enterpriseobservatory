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

- **Uzun süreli çalışma.** En uzun kesintisiz koşu birkaç saat. Bellek büyümesi,
  bağlantı havuzu davranışı, 2 günlük ham pencerenin gerçekte ne kadar yer
  kapladığı ölçülmedi.
- **Retention silmeleri.** Kova yazma doğrulandı; *silme* (2 gün / 30 gün / 400
  gün sınırları) canlıda hiç tetiklenmedi — veri o kadar eski değil.
- **Şema göçü.** Kurulum boş bir veritabanında başladı. Sürüm yükseltme yolu
  denenmedi; göç aracı da yok (ADR-0016'da kabul edilmiş borç).
- **İkinci bir vCenter.** Kimlik katlama (ADR-0003) tek kaynakla sınanıyor;
  `relationship_evidence` bu yüzden 0 satır.
- **Yetkisi kısıtlı hesap.** Bağlantı `gentel@vsphere.local` ile kuruldu.
  Salt-okunur bir servis hesabının hangi özellikleri okuyamadığı ölçülmedi —
  `missingSet` yolu kodda var ve test edildi, canlıda tetiklenmedi.
