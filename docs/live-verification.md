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
sonra retention ile temizlenecek, ardından `A_series_with_nothing_left_is_forgotten`
seri satırını da kaldıracak. Yani `series` tablosu 8 620'den 6 084'e **90 gün
içinde** inecek, bugün değil. (ADR-0017 öncesi bu 400 gündü.)

Bu doğru davranış: toplamayı bıraktık diye toplanmış ölçümü silmek, veriyi yok
etmek olurdu. Ama projeksiyon tablosu **yeni yazım hızı** içindir; disk birkaç
gün boyunca eski geçmişi de taşıyacak.

**Karar verildi:** saatlik saklama 90 gün. 13,6 GB. Ayrıntı ve kaybedilen
yıla yıl karşılaştırma için
[ADR-0017](adr/0017-retention-windows-set-by-measurement.md).

Sıradaki kısılacak yer artık saatlik değil: **beş dakikalık kademe** en büyüğü
(8 640 satır/seri, 8,5 GB). 13,6 GB bile fazla gelirse orası bakılacak yerdir.

---

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
  `A_series_with_nothing_left_is_forgotten`) ve saat ileri sarılarak 2 gün /
  30 gün / 90 gün sınırlarının üçünü de geçiyor. Doğrulanmamış olan, **canlı
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
