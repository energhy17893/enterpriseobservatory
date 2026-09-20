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

400 günlük saatlik saklama bunun için fazlasıyla yeterli. İlke 1'e uyar: sayı
gösterilir, yöntem yazılıdır, operatör kendi doğrulayabilir.

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
| Saatlik | **400 gün** | Eğilim çıkarımı, kapasite planlama |

**Kabul edilen sınır:** üç hafta önceki bir olay artık 5 dakikalık çözünürlükte
incelenebilir. Kısa süreli bir gecikme sıçraması o çözünürlükte görünmez.
Olay sonrası derin inceleme isteniyorsa ham saklama uzatılmalı — bu bir
yapılandırma kararıdır, mimari değişiklik değil.

### Açık karar: kalıcı geçmiş

Önceki ürün geçmişi **PostgreSQL'de kalıcı** tutuyordu. Bu belgedeki 400 günlük
sınır, SQLite seçiminin (ADR-0011) yan etkisi olarak oluştu; bilinçli bir ürün
kararı olarak değil. İkisi aynı şey değil ve karar yeniden verilmeli.

Mimari buna hazır: `IObservationStore` bir porttur ve somut depolama motorunu
yalnızca `Host` görebilir — mimari testle zorlanır. Bir `Persistence.Postgres`
adaptörü eklemek Domain, Application veya Api'de hiçbir değişiklik gerektirmez.

Kararı veren iki soru var:

**1. "Kalıcı" hangi çözünürlükte?** ADR-0012 her kovada min/max/sum/count/last
tutuyor ve yeniden toplama **kayıpsız** — yani saatlikten günlüğe, günlükten
aylığa indirgemek bu beş istatistiği bozmaz. Kalıcı *günlük* geçmiş on yılda bile
birkaç milyon satırdır; SQLite için bile önemsiz. Kalıcı *ham* geçmiş ise bambaşka
bir büyüklük ve ayrı bir motor ister.

> Bu ayrım, ADR-0012'de kayıtlı p95 borcunu öne çıkarıyor: yüzdelikler saklanan
> beş sayıdan türetilemez. Uzun vadeli performans raporlamasında istenen ölçü
> genellikle p95'tir, ve bugünkü kova yapısı onu **hiçbir çözünürlükte**
> veremez. Kalıcı geçmiş kararı verilirken bu birlikte karara bağlanmalı.

**2. PostgreSQL neden?** İki farklı gerekçe olabilir ve farklı işler doğururlar:
>
> - **Hacim** — veri SQLite'a sığmıyordu. Cevap: Postgres adaptörü.
> - **Erişim** — raporlama araçları, özel sorgular, mevcut DBA altyapısı.
>   Cevap: dışa aktarma veya salt-okunur bir sorgu yüzeyi. Depolama motorunu
>   değiştirmek bu ihtiyacı çözmez, sadece yerini değiştirir.

Gerekçe netleşmeden adaptör yazmak, yanlış problemi çözmek olur.

---

## 9. Bugün ne var, ne yok

| Yetenek | Durum |
|---|---|
| Topoloji, kimlik, ilişki | var |
| Zaman serisi + aşağı örnekleme | var |
| Alarm yaşam döngüsü, atıf, bakım penceresi | var |
| Topolojik olay gruplaması | var, **canlıda sınanmadı** (alarm çıkmadı) |
| vSphere performans toplama | var — cihaz başına seri dahil (20 Eylül 2026) |
| Datastore gecikmesi | yok (host'tan toplanmalı) |
| Datastore doluluk | yapılıyor |
| Yapılandırma toplama | yok |
| Uygunluk motoru | yok |
| Eşik motoru | yok |
| Teşhis merdiveni | yok |
| Olay toplama | yok |
| Eğilim çıkarımı | yok |
| İkinci satıcı (iLO/iDRAC/SAN/storage) | yok |

---

## 10. Sıralama ve gerekçesi

Sıra, **her adımın bir sonrakini mümkün kılması** ilkesine göre kuruldu.

**1. Cihaz başına seri** — ✅ *20 Eylül 2026'da yapıldı.* Merdivenin alt yarısı
buna bağlıydı: tek değere çöken bir seri "hangi LUN" sorusunu yapısal olarak
cevaplayamıyordu. Artık `disk.*` sayaçları hem özet (en kötü cihaz) hem de
cihaz başına saklanıyor — canlıda host başına 32 cihaz, estate genelinde 43
ayrı cihaz adı. CPU'ya uygulanmadı: aynı host 96 çekirdek instance'ı sunuyor
ve kimse 57. çekirdeği okuyarak teşhis koymuyor. Datastore sayaçları ise ayrı
bir durumdu — instance bir cihaz değil, başka bir **varlık**tı; onlar Datastore
varlığına taşındı (§5b).

**1b. Kalan basamak: `storagePath`** — Canlıda ölçüldü: 10 sayaç × 252 cihaz =
1135 seri **mevcut** ve toplanmıyor. SFP/kablo/zoning teşhisi için merdivenin
en alt basamağı bu. Bir sonraki doğal adım.

**2. Yapılandırma ekseni** — Uygunluğun, değişim geçmişinin ve çapraz
doğrulamanın önkoşulu. Satıcı eklemeden önce yapılmalı, yoksa her satıcı kendi
yapılandırma modelini getirir.

**3. Teşhis merdiveni (tek satıcı)** — Sadece vSphere ile bile VM/host/cluster/
datastore basamakları çalışır. Ürünün imzası burada görünür hâle gelir ve
sonraki satıcılar merdivene *basamak ekler*, yeni ürün yazmaz.

**4. Uygunluk + eşik motoru** — Tek satıcıyla bile satılabilir değer: ESXi
hardening, eskimiş snapshot, doluluk, aşırı tahsis.

**5. Eğilim çıkarımı** — Mevcut veriyle yapılabilir, yeni toplama gerektirmez.

**6. İkinci uç: iLO / iDRAC** — Kimlik katlamanın (ilke 3) ilk gerçek sınavı.

**7. SAN switch + storage** — Merdivenin en alt basamakları ve suçlama
çıkmazının diğer ucu.

**8. Çapraz doğrulama** — Yol sayısı, MPIO tutarlılığı, zoning, HCL. 6 ve 7
olmadan imkânsız.

**9. Raporlama ve sunum** — Artık raporlanacak iki ayrı şey var: sağlık/kapasite
eğilimi ve uygunluk/güvenlik durumu.

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

**Satıcı başına ayrı ekran.** Api hiçbir collector'ı görmez (mimari testle
zorlanır). Arayüz satıcı şeklinde parçalanırsa ürün, izlediği silolara dönüşür.
