# Ürün Kurgusu

Bu belge ürünün **ne olduğunu** ve **hangi yapının bunu mümkün kıldığını** tarif
eder. README ilkeleri koyar, ADR'ler tek tek kararları kaydeder; bu belge ikisinin
arasındaki boşluğu doldurur: parçaların neden bu şekilde bir araya geldiğini.

> Bu belge koddan önce gelir. Bir yetenek burada tarif edilmemişse, onu inşa
> etmeden önce buraya yazılır.

---

## 1. Ürün tezi

Sanallaştırma altyapısında bir performans sorunu yaşandığında, sorunun **hangi
katmandan geldiği** neredeyse hiçbir zaman tek bir aracın görüş alanında değildir.

Somut hâli:

> Bir VM içindeki SQL Server yavaşladı.

Bu cümlenin arkasında en az sekiz ayrı olasılık var ve bugün sahada her biri ayrı
bir ekip ve ayrı bir araç tarafından savunuluyor:

| Olasılık | Bugün kim bakar | Hangi araçla |
|---|---|---|
| VM'in kendi kaynak baskısı | Sanallaştırma | vCenter |
| Host üzerindeki **gürültülü komşu** | Sanallaştırma | vCenter |
| Cluster dengesizliği / DRS | Sanallaştırma | vCenter |
| Datastore gecikmesi | Sanallaştırma + Storage | ayrı ayrı |
| LUN / cihaz gecikmesi | Storage | dizi arayüzü |
| **SFP veya kablo hatası** | SAN | switch CLI |
| **Zoning yapılandırması** | SAN | switch CLI |
| Dizi tarafı darboğaz | Storage | dizi arayüzü |

Hiçbiri diğerini göremediği için sonuç, herkesin bildiği **suçlama çıkmazıdır**:
sunucu ekibi storage'ı, storage switch'i suçlar, kesinti uzar.

**Ürünün tezi:** bu sekiz olasılık tek bir veri modelinde, tek bir zaman
ekseninde ve tek bir topoloji üzerinde toplandığında, soru *tartışma* olmaktan
çıkıp *ölçüm* hâline gelir.

Ürün bir VMware izleme aracı değildir. **Depolama ve hesaplama yolu üzerinde
arıza yerelleştirme** aracıdır; vCenter o yolun yalnızca bir ucudur.

---

## 2. Ürünün cevapladığı dört soru

Bu ayrım kurgunun temelidir. Dört soru **farklı türde** sorulardır ve bunları tek
bir mekanizmaya bindirmek, ürünü ya gürültüye boğar ya da sessizleştirir.

| # | Soru | Biçim | Ömür | Kapanma |
|---|---|---|---|---|
| 1 | **Sağlıklı mı?** | Alarm | Dakikalar–saatler | Koşul geçince otomatik |
| 2 | **Doğru kurulmuş mu?** | Bulgu | Aylar | İnsan düzeltir **veya** riski kabul eder |
| 3 | **Neden yavaş?** | İnceleme | Anlık | Cevap verilince biter |
| 4 | **Sırada ne bozulacak?** | Öngörü | Sürekli | Yeniden hesaplanır |

Bugün ürün yalnızca **1**'i yapıyor. Kurgunun geri kalanı 2, 3 ve 4'ün neye
ihtiyaç duyduğunu tarif eder.

### Neden ayrı olmak zorundalar

**Uygunluk bulgusu alarm değildir.** Bir uygunluk motoru ilk çalıştığında yüzlerce
bulgu üretir — hepsi doğrudur ve hiçbiri acil değildir. Alarm kutusuna
dökülürse, ilke 4'ü ("gürültü operatörün düşmanıdır") ilk gün ihlal eder ve
operatör kutuya bakmayı bırakır. Bulgu, **kabul edilebilir** olmak zorundadır;
alarm değildir.

**İnceleme saklanmaz.** "Bu VM neden yavaş" sorusunun cevabı bir kayıt değil, o
an topoloji üzerinde yürünerek hesaplanan bir açıklamadır. Saklanırsa eskir.

---

## 3. Kapsam

Her katman iki eksende ele alınır: **ne ölçüyoruz** ve **doğru mu kurulmuş**.
Üçüncü sütun ürünün asıl farkıdır.

| Katman | Performans | Yapılandırma / best practice | Çapraz doğrulama |
|---|---|---|---|
| vCenter | oturum, görev kuyruğu | Rol/izin, sertifika, SSO, yedekleme | — |
| ESXi host | CPU, bellek, disk, ağ | Hardening guide, NTP, syslog, lockdown | Cluster içi **tutarlılık** |
| VM | cpu.ready, costop, balloon, swap, vDisk | Donanım sürümü, Tools, **eskimiş snapshot**, aşırı tahsis | EVC / vMotion uyumluluğu |
| Datastore | Gecikme, IOPS, doluluk | VMFS sürümü, thin overcommit | Tüm host'lar aynı datastore'u görüyor mu |
| LUN / cihaz | device/kernel/queue gecikmesi | Kuyruk derinliği | Yol sayısı ≥ 2 |
| HBA | Adaptör gecikmesi | Firmware / driver | **HCL uyumluluğu** |
| SAN yolu | Yol başına gecikme | MPIO politikası | **Zoning ↔ gerçek yol** |
| SAN switch | Port, CRC, SFP ışık gücü | Zoning, port ayarları | Switch portu ↔ ESXi HBA |
| Storage dizisi | Volume gecikmesi, doygunluk | Replikasyon, snapshot politikası | Dizi volume ↔ ESXi LUN |
| iLO / iDRAC | Sensörler, güç, ısı | Firmware, BIOS ayarları | BMC sağlığı ↔ ESXi görüşü |
| Ağ | pNIC hata/düşme | MTU, teaming, VLAN | Switch portu ↔ vSwitch uplink |

Sol iki sütunu yapan çok araç var. **Sağ sütunu yapan yok** — ürünün satılabilir
farkı orada.

---

## 4. Teşhis modeli — ürünün imzası

Soru 3'ün ("neden yavaş") cevabı **makine öğrenmesi değildir.** Yazılı,
gözden geçirilebilir, test edilebilir bir **nedensellik merdivenidir**.

### Kural

Her katman için iki şey tanımlanır:

- **Suçlayan kanıt** — bu katman sebepse görülmesi gereken ölçü
- **Aklayan kanıt** — bu katman sebep *değilse* görülmesi gereken ölçü

Merdiven yukarıdan aşağı yürünür ve her basamak **suçlandı / aklandı /
bakılamadı** döner. Üçüncüsü ikisinden farklıdır ve ilke 1 gereği açıkça öyle
raporlanır.

### SQL Server senaryosu, basamak basamak

| Basamak | Suçlayan | Aklayan | Kaynak |
|---|---|---|---|
| VM'in kendi CPU baskısı | `cpu.ready` yüksek | düşük | VM, 20s |
| SMP zamanlama | `cpu.costop` yüksek | düşük | VM, 20s |
| VM bellek baskısı | `mem.vmmemctl`, `mem.swapped` > 0 | sıfır | VM, 20s |
| **Gürültülü komşu** | Kardeş VM'lerin `cpu.ready` toplamı yüksek, host CPU doygun | kardeşler sakin | **Kardeş = aynı host'a `RunsOn`** |
| Host bellek baskısı | Host `mem.swapused` > 0 | sıfır | Host, 20s |
| Cluster dengesizliği | Host, küme ortalamasının çok üstünde | dengeli | `PartOf` ile küme |
| Sanal disk | `virtualDisk.total*Latency` yüksek | düşük | VM, 20s |
| Datastore | `datastore.total*Latency` yüksek | düşük | **Host**, VMFS UUID instance |
| LUN / cihaz | `disk.deviceLatency` yüksek | düşük | Host, `naa.*` instance |
| Kuyruk / doygunluk | `disk.queueLatency` yüksek | düşük | Host, `naa.*` instance |
| **SAN yolu** | Tek yol yüksek, kardeş yollar sakin | tüm yollar benzer | Host, `initiator:target:LUN` |
| HBA | Adaptör geneli yüksek | tek yol | Host, `vmhbaN` |
| Switch / SFP | CRC, ışık gücü düşük | temiz | SAN switch |
| Dizi | Volume gecikmesi yüksek | düşük | Storage |

**Merdivenin gücü ayrıştırmadadır:** `disk.deviceLatency` yüksek ama
`disk.queueLatency` düşükse sorun dizide veya fabric'tedir, host'ta değil.
Tersi ise host doygunluğudur. Bu ayrım ürünün metrik sözleşmesinde zaten yazılı
ve üçlünün toplanma sebebidir.

**Tek yol yüksek, kardeşleri sakin** deseni, bir SFP veya kablo arızasının
imzasıdır. Bunu görebilmek için yol başına seri gerekir — bugün yok, ve
`storagePath` sayaçları **istatistik seviyesi 3** ister.

### Gürültülü komşunun eşikle ölçülemeyeceği

"CPU ready %5'in üstünde" bir eşiktir ve yanlış cevap verir: aynı değer boş bir
host'ta anlamsız, dolu bir host'ta felakettir. Doğru soru **karşılaştırmalıdır** —
bu VM'in `cpu.ready` değeri, *aynı host üzerindeki kardeşlerine* ve *aynı
kümedeki diğer host'lara* göre nerede duruyor?

Bunun için gereken üç şey bugün **zaten var**: VM başına seri, `RunsOn` ilişkisi
ve zaman hizalaması. Yani gürültülü komşu tespiti yeni veri değil, yeni bir
**sorgu** gerektiriyor.

---

## 5. Veri eksenleri

Ürünün tutması gereken beş ayrı şey var. Üçü var, ikisi yok.

| Eksen | Ne | Bugün |
|---|---|---|
| **Topoloji** | Varlıklar, ilişkiler, kimlik işaretleri | **var** (ADR-0003/0004) |
| **Zaman serisi** | Sayısal ölçümler | **var** (ADR-0012) |
| **Alarm durumu** | Yaşam döngüsü, atıf, bakım | **var** (ADR-0007/0013) |
| **Yapılandırma anlık görüntüsü** | "Bu nesne şu an nasıl kurulmuş" + **değişim geçmişi** | **yok** |
| **Olaylar** | Alt sistemlerin yapılandırılmış olay kayıtları | **yok** |

### Yapılandırma neden ayrı bir eksen

Uygunluk kontrolü yapılandırma **durumunu** gerektirir; ölçüm değil. Ve
değişim geçmişi, teşhisin en güçlü tek sorusunu mümkün kılar:

> "Bu yavaşlık ne zaman başladı? O tarihte ne değişti?"

Yapılandırma anlık görüntüsü olmadan bu soru cevaplanamaz. Olduğunda,
"dün 14:20'de bu host'un MPIO politikası değişti" cevabı teşhis süresini
saatlerden dakikalara indirir.

### Log değil, olay

"Alt sistemlerin loglarını karşılıklı analiz etmek" hedefi doğru; ama ham log
toplamak ürünü bir log platformuna çevirir — ayrı depolama motoru, tam metin
arama, disk maliyeti, ve ürünün asıl işinden sapma.

**Önerilen yol:** ham log yerine **yapılandırılmış olay**. vCenter events, ESXi
donanım olayları, switch RASlog, dizi uyarıları. Bunlar düşük hacimli, yüksek
sinyalli, ve zaten *zaman + varlık* ile anahtarlı — yani mevcut korelasyon
motoruna doğrudan girerler.

Ham log gerekirse sonra eklenir; olayla başlamak, değerin %80'ini maliyetin
%10'una verir.

---

## 6. Uygunluk motoru

### Bulgunun yaşam döngüsü

Alarmdan farklıdır ve bu fark modellenmelidir:

```
açık ──> düzeltildi (yapılandırma değişti, kontrol tekrar geçti)
     └─> kabul edildi (kim, ne zaman, neden, ne zamana kadar)
```

**Kabul, süreli olmalıdır.** Süresiz istisna, unutulmuş istisnadır; ve unutulmuş
istisna denetimde en pahalı bulgudur. Bu, bakım penceresindeki disiplinin
(ADR-0013: kim, ne zaman, neden) uygunluğa taşınmış hâlidir.

### Kural, hem yapılandırmayı hem ölçümü okuyabilmeli

Çapraz best practice'ler ikisini birden ister:

- "Kuyruk derinliği 64 **ama** gerçek bekleyen IO sürekli 200" → ayar yanlış
- "MPIO politikası Round Robin **ama** yolların biri hiç kullanılmıyor" → yol bozuk
- "Datastore %70 dolu **ama** thin provisioned toplam %180" → aşırı taahhüt

Yani kural motoru, eşik motorunun üst kümesidir. İkisi ayrı yazılmamalı.

---

## 7. Öngörü — dürüst ayrım

"İleriye dönük sorunları önceden tahmin etmek" iki farklı şeydir ve birini
diğeriyle karıştırmak ürüne zarar verir.

### Yapılacak: eğilim çıkarımı

Ölçülebilir, açıklanabilir, yanlış olduğunda neden yanlış olduğu görülebilir:

- "Bu datastore mevcut büyüme hızıyla **12 gün** içinde dolar"
- "Bu LUN'un gecikmesi üç haftadır tırmanıyor"
- "Bu host'un bellek baskısı her salı artıyor"

90 günlük saatlik saklama bunun için yeterli: üçü de en fazla haftalık desen
ya da birkaç haftalık eğim istiyor, ve 90 gün on iki salı demek. İlke 1'e uyar:
sayı gösterilir, yöntem yazılıdır, operatör kendi doğrulayabilir.

### Şimdilik yapılmayacak: anomali/ML

Yanlış pozitif üretir, neden ürettiğini açıklayamaz, ve ilke 1 ile ("asla
uydurma") doğrudan gerilim içindedir. Bir izleme aracının söylediği şeyi
gerekçelendirememesi, operatörün güvenini kaybettiği andır.

Eğilim çıkarımı ve çapraz karşılaştırma, ML'in vaat ettiği değerin büyük kısmını
**açıklanabilir** biçimde verir. ML, o tükendikten sonra tartışılır.

---

## 8. Saklama süresinin kurguya etkisi

ADR-0012'deki saklama politikası teşhis derinliğini doğrudan belirler:

| Çözünürlük | Süre | Neyi mümkün kılar |
|---|---|---|
| Ham (~30s) | **2 gün** | Dünkü olayın saniye seviyesinde incelenmesi |
| 5 dakika | **30 gün** | Haftalık desen, komşu karşılaştırması |
| Saatlik | **90 gün** | Eğilim çıkarımı, kapasite planlama |

**Kabul edilen sınır:** üç hafta önceki bir olay artık 5 dakikalık çözünürlükte
incelenebilir. Kısa süreli bir gecikme sıçraması o çözünürlükte görünmez.
Olay sonrası derin inceleme isteniyorsa ham saklama uzatılmalı — bu bir
yapılandırma kararıdır, mimari değişiklik değil.

### Kapanan karar: saklama süresi artık bilinçli

Bu bölüm "400 günlük sınır SQLite seçiminin (ADR-0011) yan etkisiydi, bilinçli
bir ürün kararı değildi; karar yeniden verilmeli" diyordu.

**20 Eylül 2026'da yeniden verildi** ([ADR-0017](adr/0017-retention-windows-set-by-measurement.md)).
Bu kez ölçümle: satır başına bayt sayıldı, kademe başına satır hesaplandı, ve
ADR-0012'nin "uzun kuyruk neredeyse bedava" gerekçesinin yapılandırılmış
saklama süreleriyle **yürümediği** görüldü — saatlik kademe üç kademenin en
büyüğüydü. Saatlik saklama 90 güne indi; kararlı durum 20,9 → 13,6 GB.

Ödenen bedel kayıtlı: **yıla yıl karşılaştırma bitti.** Mevsimsellik gerekirse
cevap saatlik pencereyi uzatmak değil, **günlük dördüncü kademedir** — seri
başına yılda 365 satır, 400 günlük saatlikten **24 kat** ucuz (9 600 / 400).
Bu bölüm ve ADR-0017 önceden "kırk kat" diyordu; çarpan bir gündeki saat
sayısıdır ve 24'tür, düzeltme [ADR-0023](adr/0023-postgres-dependency-rejustified.md)'dedir.
Ölçülen estate için maliyeti: beş yıllık günlük geçmiş ≈ **1,79 GB**
(6 084 seri × 1 825 satır × 173,1 bayt), yani ADR-0017'nin kestiği 7,3 GB'ın
dörtte birinden azı.

### Kapanan karar: depolama motoru seçildi, adaptör yazıldı, gerekçe taşındı

Bu bölüm önceden *"PostgreSQL neden?"* diye soruyor ve *"gerekçe netleşmeden
adaptör yazmak, yanlış problemi çözmek olur"* diyordu. İkisi de kapandı.

**Motor seçildi.** [ADR-0016](adr/0016-external-infrastructure-dependencies.md)
(20 Eylül 2026) PostgreSQL'i tek depolama motoru yaptı ve ADR-0011'i geçersiz
kıldı; "ek appliance gerektirmez" bir kısıt olmaktan çıktı. **Adaptör yazıldı:**
`src/EnterpriseObservatory.Persistence.Postgres/` — 11 dosya, 23 tablo, 7 store.

**Gerekçe ise bir kez daha değişti.** ADR-0016 hacmi başa koymuştu ("kalıcı
ölçüm geçmişi"); ADR-0017 aynı gün saatlik kademeyi kesince o bacak düştü.
[ADR-0023](adr/0023-postgres-dependency-rejustified.md) gerekçeyi **yazma
yoluna** taşıdı: ikili `COPY`, `unnest` ile dizi bağlama, `FOR KEY SHARE` ve
`FOR UPDATE SKIP LOCKED` — tahmin edilen hacim değil, çalışan kod. Kalıcı geçmiş
vaadi boş bırakılmadı; günlük dördüncü kademeyle karşılanacak (ADR-0023 karar 3).

Mimari bunu ucuz kıldı ve iddiasını ikinci kez doğruladı: `IObservationStore`
bir porttur, somut depolama motorunu yalnızca `Host` görür — mimari testle
zorlanır — ve Postgres adaptörü Domain, Application veya Api'de hiçbir değişiklik
gerektirmedi. Aynı ölçüm tersi yöne de geçerli: motoru bir gün değiştirmek
adaptör ve testlerinin işidir, mimarinin değil.

**Açık kalan soru çözünürlük değil, istatistiktir.** ADR-0012 her kovada
min/max/sum/count/last tutuyor ve yeniden toplama **kayıpsız** — saatlikten
günlüğe, günlükten aylığa indirgemek bu beş istatistiği bozmaz. Ama:

> Yüzdelikler saklanan beş sayıdan türetilemez. Uzun vadeli performans
> raporlamasında istenen ölçü genellikle p95'tir, ve bugünkü kova yapısı onu
> **hiçbir çözünürlükte** veremez. Günlük kademe kararı verilirken bu birlikte
> karara bağlanmalı.

---

## 9. Bugün ne var, ne yok

*21 Eylül 2026 güncellendi. Bu tablo her tur eskiyor, ve eskimesi bedava
değil: önceki hâli birleştirilmeden beklerken bir ajan §10'da adım 1d'yi
arayıp bulamadı ve olmadığını varsaydı. Birleşmemiş bir belge, olmayan bir
belgedir.*

| Yetenek | Durum |
|---|---|
| Topoloji, kimlik, ilişki | var |
| Zaman serisi + aşağı örnekleme | var |
| Alarm yaşam döngüsü, atıf, bakım penceresi | var |
| Topolojik olay gruplaması | var, **canlıda sınanmadı** |
| vSphere performans toplama | var — cihaz başına seri dahil |
| Datastore gecikmesi | **var** — host'tan toplanıp Datastore varlığına taşınıyor (§5b) |
| Datastore doluluk | var; **aşırı taahhüt de var** (`summary.uncommitted`) |
| Teşhis merdiveni | **kısmen** — beş basamak: akran aykırılığı, dizi, katman ayrımı, CPU çekişmesi, yol yedekliliği |
| Eşik motoru | **kural başına politika var**, merkezî motor yok |
| Yapılandırma toplama | **kısmen** — snapshot, VM boyutlandırma, CPU/bellek limitleri, multipath, HA/DRS |
| Uygunluk motoru | yok — ham maddesi hazır: Broadcom kontrol listesini sürümlü CSV olarak yayımlıyor |
| Olay toplama | yok |
| Eğilim çıkarımı | yok |
| İkinci satıcı (iLO/iDRAC/SAN/storage) | yok |

### Ölçülmüş bir sınır

Depolama kurallarının üçü **bu estate'te yapısal olarak ateşleyemez.** Probe ile
ölçüldü: `datastore.total{Read,Write}Latency` 302 okumanın 302'sinde sıfır,
`disk.device/kernel/queueLatency` 33 okumanın 33'ünde sıfır — ama IOPS sıfır
değil. SIOC kapalı ve platform 1 ms altını sıfıra kırpıyor.

Kurallar yine de duruyor: dizinin gerçekten bozulduğu bir estate'te oradalar.
Ama **sessizlikleri sağlık sanılmamalı**, ve `StorageLatencyBlindSpot` tam
olarak bunu söylemek için yazıldı.

---

## 10. Sıralama ve gerekçesi

Sıra, **her adımın bir sonrakini mümkün kılması** ilkesine göre kuruldu.

### Her adım referansla başlar

Bu bir üslup tercihi değil, ölçülmüş bir sonuç. Kural yazma turunda dört ardışık
"makul" tasarım yanlış çıktı ve üçü referans literatürde zaten yazılıydı. Bir
adıma başlamadan önce vROps/Aria, Dynatrace, Datadog ve açık kaynak tarafının o
konuda **ne yaptığı ve neyi bilerek yapmadığı** çıkarılır.

İki uyarı, ikisi de bu turda bedel ödetti:

- **Eşikler belgelerde yok.** vROps tüm vCenter çözümünde iki sayısal eşik
  yayınlıyor. Dynatrace yapıyı yayınlıyor, seviyeleri değil. Alıntılayamadığın
  bir eşiği kullanma; ya bu üründe gözden geçirilmiş bir değerden ödünç al, ya
  yapısal bir şey seç, ya da *"bu benim seçimim"* diye işaretle.
- **Kopyalanmayacaklar listesi de çıkarılır.** vROps'un on iki neredeyse-aynı
  contention alarmı, on bir ayrı sensör alarmı ve vSphere 5.5'e sabitlenmiş
  hardening tanımları bilerek alınmadı. Referans, taklit edilecek bir liste
  değil; birinin bizden önce ödediği öğrenme maliyeti.

**1. Cihaz başına seri** — ✅ *20 Eylül 2026.* Merdivenin alt yarısı buna
bağlıydı. CPU'ya uygulanmadı: 96 çekirdek instance'ı var ve kimse 57. çekirdeği
okuyarak teşhis koymuyor.

**1b. `storagePath`** — ✅ *kısmen yapıldı, kısmen reddedildi.* Arıza sayaçları
(`busResets`, `commandsAborted`) toplanıyor ve `FaultCounters`'tan geçiyor.
Gecikme sayaçları ölçülüp **çıkarıldı**: 2.536 seri, ~8 GB, ve §5b'nin
cevaplanamaz olduğunu gösterdiği bir soru. Kalan bilinen maliyet: bir bus reset
host ve HBA'ya atfedilebiliyor ama datastore'a otomatik atfedilemiyordu —
`multipathInfo` + `scsiLun` artık toplandığı için bu kapatılabilir hâle geldi.

### 0. Kapanmamış borçlar

Hepsi kayıtlı ve küçük. Yeni yetenek başlamadan önce, çünkü her biri var olan
bir yeteneğin **sessizce** yanlış çalışmasına izin veriyor.

- **`ChangeOwnPassword` mutate dışında doğruluyor.** Bir yöneticinin sıfırlaması
  o aralığa denk gelirse kullanıcının kendi değişikliğiyle ezilir.
- **`cpu.maxlimited.summation` canlıya karşı doğrulanmadı.** Diğer doğrulanmamış
  sayaçlar adı yanlışsa bir cevabı kaybeder; bu sayaç **baskılamayı durdurur** ve
  `CpuContention` kapatmak için eklendiği yanlış pozitife sessizce geri döner.
- **Yük kapısının çalkalanması** (ADR-0021). SIOC'si kapalı bir volume tek turda
  boşta kalırsa temizlenmiş alarmı emekliye ayrılıyor ve döndüğünde yeniden
  bildiriyor — dalgalı volume'larda toplu temizlemeler aşınıyor.
- **`mem.state.latest` okunmuyor.** Sayaç toplandı, kuralı yok. İhtiyacı olan şey
  bir bayrak değil, toplayıcının bildirdiği bir **eşik değeri**
  (`DegradedAtOrAbove`) — çünkü burada sıfır *sağlıklı* demek ve bayrağın
  "sıfır = olmadı" vaadini tersine çevirir.
- **Anahtar halkası koruması log, alarm değil** (ADR-0020, Alternatif D).

**Kapanan bir borç, ve nasıl bulunduğu borcun kendisinden öğretici.**
`CpuContention` ready süresini vCPU sayısına **bölmüyordu**. `Entity.cs` bunu
alanı eklendiğinden beri yazıyordu — *"an eight-way machine at a genuinely
healthy 2% reads as 16%"* — ve kural `VirtualCpuCount`'tan hiç söz etmiyordu.
Asıl kusur eşiklerde değil kardeş karşılaştırmasındaydı: karışık genişlikli bir
host'ta `ready > çarpan × medyan`, çekişme kostümü giymiş bir **vCPU sayısı
karşılaştırması**, ve en geniş makine şekli yüzünden suçlanıyordu.

Bunu bulan ne bir test ne bir alarm oldu — **dışarıdan bir inceleme** oldu.
Ders şu: bir uyarıyı yazmak, ona uyulduğunu göstermiyor. Mevcut 44 testin
24'ü hiç genişlik kurmuyordu, yani kural bugüne kadar **normalize edilmemiş
sayılarla** sınanmıştı ve takım bundan memnundu.

### 1. Teşhis merdivenini tamamla — ürünün imzası

Referans araştırması bir **pazar boşluğu** buldu: vROps, Dynatrace ve Datadog'un
hiçbiri gürültücü komşuyu adlandırmıyor. Üçü de kurbanı ve host'u görüyor;
Dynatrace VM'in kimliğini eline alıp host seviyesinde bir probleme çevirip
atıyor. Zor yarısı — instance→varlık eşlemesi ve gözlem-noktası modeli —
`PeerOutliers` ile zaten kanıtlandı.

- **1a. Gürültücü komşu, CPU.** Host doygun **ve** ≥3 misafir **ve** birinin payı
  medyanın N katı **ve** o makinenin kendi ready'si düşük. Susturan: en çok
  tüketen *de* bekliyorsa o da kurbandır ve host yetersizdir, zehirli değil.
  `cpu.usagemhz.average` gerekiyor.
- **1b. Gürültücü komşu, datastore.** Volume her host'tan yavaş **ve** IOPS
  gecikmeyle birlikte yükselmiş **ve** bir makinenin vDisk IOPS'u akran
  medyanının N katı. Susturan: IOPS yükselmediyse dizi sabit yük altında
  bozuluyor demektir ve bir VM'i suçlamak kendinden emin yanlış cevaptır. Ayrıca
  **vMotion penceresi** — göç bu imzayı meşru olarak üretir, ve Dynatrace'in
  göç olaylarını zaman çizelgesi bağlamı olarak kullanması ucuz bir kapıdır.
- **1c. Gerçek bellek baskısı.** Swap-in/out oranı **veya** sıkıştırma/açma
  oranı. `mem.usage` **değil** ve balon tek başına **değil**: balon erken ve
  yüksek yanlış-pozitifli, swap geç ve neredeyse hatasız — Datadog ile
  Dynatrace'in açıkça ayrıştığı yer burası ve ayrışma cevabın kendisi. Sayaçlar
  toplandı, `mem.active` de balonun yanlış pozitifini kapatmak için orada.
- **1d. Yol yedekliliği** — ✅ *21 Eylül 2026.* `StoragePathRedundancy`. Üç
  kova, iki değil: `active`/`standby` çalışıyor, `dead` arızalı, ve
  `disabled`/`unknown`/boş **hiçbiri değil** — "çalışıyor"u "ölü değil" diye
  yazmak vCenter her cevap vermediğinde kesinti raporlardı. Tek yollu LUN
  sessiz kalıyor ve bunun için bir kapı gerekmedi: "yedeklilik kayboldu" bir
  ölü **ve** bir çalışan yol istiyor, bir yol ikisi birden olamaz. *"Bu LUN'un
  iki yolu olmalı"* ise alarm değil **bulgu** — yeri adım 4.
  **Canlıda henüz ateşlemedi ve tel şekli hiç dökülmedi**; tanınmayan bir durum
  iki listenin de dışında, yani kural yanlışsa sessiz kalıyor.
- **1e. Düşen paketler.** Sayaçlar toplandı ve bilerek **arıza değil seviye**
  olarak işaretlendi: meşgul bir uplink çerçeve düşürür, SCSI dizi meşgul diye
  bus reset atmaz. Yani eşik ve oran gerekiyor, `FaultCounters` yolu değil.

### 2. Olay toplama

vCenter **event stream**'i — alarm geçişi değil. Tek bir bağımlılık altı
yeteneğin kilidini açıyor: beş HA alarmı (host izole, olası host arızası,
başarısız failover, yetersiz failover kaynağı, kayıp master), NFS bağlantı
kaybı, pNIC flapping ve uplink yedekliliği. Bugünkü `triggeredAlarmState` geçişi
bunları yalnızca vCenter *ayrıca bir alarm da* yükselttiğinde yakalıyor, ki aynı
küme değil.

**Belgedeki sıradan tek sapma bu:** olay toplama yapılandırma ekseninin önüne
alındı. Gerekçe, bağımlılık oranı — tek iş, altı yetenek — ve yapılandırma
ekseninin model kararının acele edilmemesi gerektiği.

### 3. Yapılandırma ekseni

Önkoşul gerekçesi değişmedi: satıcı eklemeden önce yapılmalı, yoksa her satıcı
kendi yapılandırma modelini getirir. Ama artık bir başlangıç var ve **modeli yok**
— özellikler `Entity` üzerine tek tek eklendi.

- **3a.** Yapılandırma zaman serisi değil, **durum geçmişi**. "Bu ayar ne zaman
  değişti" sorusu metrikten farklı bir depolama istiyor.
- **3b.** `Entity.Sizing` ve `Entity.StoragePaths` **kalıcı değil** — yeniden
  başlatmadan sonra bir envanter turuna kadar null. `Sizing` dört sütun,
  `StoragePaths` yeni bir tablo.
- **3c.** Toplanacaklar: host gelişmiş ayarları, NTP, syslog hedefi, VM donanım
  sürümü, cluster HA admission control politikası.

### 4. Uygunluk motoru

Snapshot ve aşırı taahhüt **kural olarak** çalışıyor ama §6'daki bulgu yaşam
döngüsü yok. Uygunluk bulgusu alarmdan farklı bir şeydir: kendiliğinden kapanmaz,
kabul edilir, ve istisnası olur.

Referans uyarısı burada özellikle sert: vROps'un hardening-guide alarmları hâlâ
**vSphere 5.5**'e sabitlenmiş ve 8.18'de gönderilmeye devam ediyor. Uygunluğu
alarm olarak modellemenin bedeli budur; kendi yaşam döngüsü olan bir motor
gerekiyor.

### 5. Eğilim çıkarımı

Datastore kapasitesi **seri olarak toplanmıyor** — yalnızca anlık envanter
okuması var, yani *"altı gün sonra dolar"* cevaplanamıyor. Önce
`disk.capacity/used/provisioned.latest` (seviye 1, Datastore nesnesinde).
ADR-0012 persentili yasaklıyor, ama doğrusal eğilim persentil istemiyor.

### 6. İkinci uç: iLO / iDRAC

Kimlik katlamanın (ilke 3) ilk gerçek sınavı.

### 7. SAN switch + storage → 8. Çapraz doğrulama → 9. Raporlama

Değişmedi.

### 10–12. Genişleme: süreklilik, maruziyet, öngörü v2 *(21 Eylül 2026)*

Yol haritası o güne kadar neredeyse tamamen **arıza ve teşhis**ti. Dört kollu
referans araştırması ([reference-approaches §9](reference-approaches.md)) üç
set ve M6/M7 ekleri çıkardı; adımlar `feature-roadmap.md`'de M8, M9, M10.

- **Süreklilik duruşu (M8)** soru 2'nin ("doğru kurulmuş mu") ikinci yarısı:
  SCG *güvenli mi* diye soruyor, bu set *bir şey düştüğünde ayakta kalır mı*
  diye. Raporlamadan hemen sonra, ikinci satıcıdan **önce** yürür: neredeyse
  tamamı eldeki veriyle cevaplanıyor — gürültücü komşudaki gibi yeni veri değil,
  yeni sorgu — ve çıktısı alıcının okuyacağı bir rapor. SPOF kontrolleri
  §3'teki çapraz doğrulama sütununun vim25'le yapılabilen kısmı.
- **Maruziyet ve yaşam döngüsü (M9)** uygunluk motorunun ikinci kataloğu: VMSA,
  KEV ve destek tarihleri SCG CSV'si gibi **veri olarak** yutulur. Skyline
  Advisor'ın kapanması ve Runecast'in yeniden konumlanması on-prem sağlık
  kontrolünde boşluk bıraktı.
- **Öngörü v2 (M10)** §7'nin içinde kalır: hepsi eğilim, eşik ve aritmetik;
  ML yok. İlk adım değişim noktası, çünkü M4'ün dolma tarihleri bir göçten sonra
  bugün de yanlış eğim okur. Haftanın saati bantları §7'nin sınırındadır ve
  ADR'siz yapılmaz.
- **Donanımda tarih yalnızca aşınmaya verilir.** ECC ve SMART bir **risk
  bayrağını** destekler, arıza tarihini desteklemez — ilke 1.

Dördüncü ilke burada da geçerli: çok ateşleyen kontrol (Tools sürümü, eski
adaptör) bulgu değil görünüm ya da sayısıyla tek bulgu olur.

### Çapraz kesen: kompozisyon kökü testi

`Program.cs` 406 satır ve **hiçbir testi yok**. `UseAuthorization()`'ı silmek her
`RequireAuthorization`'ı no-op yapıyor, `OnValidatePrincipal` handler'ını silmek
kaldırılmış bir hesaba 12 saat erişim bırakıyor — ve hiçbir test düşmüyor. Bir
`WebApplicationFactory` duman takımı gerekiyor: anonim istek 401, Viewer'ın yazma
denemesi 403, oturum ortasında kaldırılan hesap 401.

Bir sonraki commit'ten önce değil, **bir sonraki sürümden önce**. Kimseyle
çakışmadığı için paralel yürür.

---

## 11. Reddedilen yaklaşımlar

**Önce ML.** Açıklanamayan bir tespit, ilke 1'i ihlal eder. Eğilim ve
karşılaştırma tükenmeden ML tartışılmaz.

**Ham log platformu.** Ürünü depolama ve arama problemine çevirir. Yapılandırılmış
olay, değerin büyük kısmını çok daha ucuza verir.

**Misafir içi ajan.** SQL Server'ın içine bakmak cazip ama ürünün sınırı
hipervizör ve altıdır. Ajan, ilke 5'in ("izleme aracı üretimi bozmaz") en zor
sınandığı yerdir ve bu ürünün farkı orada değil.

**Uygunluğu alarm olarak modellemek.** İlk gün yüzlerce bulgu, ilk hafta
kapatılmış bir alarm kutusu.

**KB ↔ log eşleştirme ve HCL hükmü.** İlki syslog ve sürekli bir içerik ekibi
ister; ikincisinin resmî veri beslemesi yok. Yerine küme içi sapma.

**Sayaçtan arıza tarihi.** ECC/SMART sayaçları bayrak üretir; tarih uydurmak
ilke 1'in ihlalidir.

**Satıcı başına ayrı ekran.** Api hiçbir collector'ı görmez (mimari testle
zorlanır). Arayüz satıcı şeklinde parçalanırsa ürün, izlediği silolara dönüşür.
