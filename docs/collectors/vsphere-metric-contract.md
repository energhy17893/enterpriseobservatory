# vSphere Metrik Sözleşmesi

Bu belge, vSphere collector'ının **hangi metriği, hangi API yüzeyinden, hangi
granülerlikte ve hangi anlamla** topladığını tanımlar.

Amacı, kod yazılmadan önce şu sorulara cevap vermektir:

1. Ürünün vaat ettiği yetenekler için hangi metrikler *gerçekten* gerekli?
2. Her metrik hangi protokol yüzeyinden alınabilir ve maliyeti nedir?
3. Bir metriğin *anlamı* nedir — bir ortalama mı, toplam mı, anlık değer mi?
4. Hangi tuzaklar var ve onlara karşı ne yapıyoruz?

> **Bu belge koddan önce gelir.** Bir metrik burada tanımlı değilse collector
> onu toplamaz. Yeni metrik ihtiyacı önce bu belgeye eklenir.

---

## 1. vSphere'de metrik nasıl üretilir?

Doğru collector yazmak için altyapının veriyi nasıl ürettiğini bilmek şart.

**Toplama ve yuvarlama zinciri:**

| Katman | Ne yapar | Saklama |
|---|---|---|
| ESXi host | Her **20 saniyede** bir örnekler | ~1 saat, host belleğinde |
| vCenter | Host'lardan toplar, yuvarlar (rollup) | Veritabanında, aşağıdaki aralıklarda |

vCenter dört **tarihsel aralık** tutar:

| Aralık | Çözünürlük | Tipik saklama |
|---|---|---|
| 1 | 5 dakika | 1 gün |
| 2 | 30 dakika | 1 hafta |
| 3 | 2 saat | 1 ay |
| 4 | 1 gün | 1 yıl |

Buna ek olarak **gerçek zamanlı** (real-time) besleme vardır: `intervalId = 20`,
doğrudan ESXi host'tan okunur, vCenter veritabanını **baypas eder**.

**Kritik sonuç:** Hangi metriğin hangi aralıkta mevcut olduğu, vCenter'daki
**istatistik seviyesi** (1–4) ayarına bağlıdır. Seviye 1 varsayılandır ve çoğu
ayrıntılı sayacı içermez. Bir sayaç seviye 3 gerektiriyorsa ve müşteri seviye
1'de çalışıyorsa, **o sayaç boş döner** — hata vermez, sessizce yok olur.

Kaynak: [vSphere Performance Data Collection](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/8-0/web-services-sdk-programming-guide/vsphere-performance/vsphere-performance-data-collection.html)

## 1.1 Yüzdeler yüzde birin yüzde biri cinsinden gelir

vSphere, birimi `percent` olan sayaçları **100 ile ölçekli** döndürür. Canlı bir
vCenter 8'den gözlenen:

| Ham değer | Birim | Gerçek |
|---|---|---|
| 2669 | `percent` | %26,69 |
| 1441 | `percent` | %14,41 |

Ham değeri olduğu gibi raporlamak ilk döngüde her eşiği tetikler. Dönüşüm
**adaptörde** yapılır (`VsphereUnitNormalizer`), aşağıda değil: `CounterValue.Raw`
"bu sayacın belirtilen birimdeki değeri" demektir ve satıcının kodlamasını bilen
adaptördür. Ölçeklemeyi bir tüketicinin hatırlamasına bırakmak, bir tüketicinin
unutmasının yoludur.

Yalnızca doğrulanmış dönüşüm uygulanır; tanınmayan birim dokunulmadan geçer.

## 2. Rollup tipi metriğin anlamıdır

Her sayacın bir `rollupType`'ı vardır ve bu, değerin **ne anlama geldiğini**
belirler:

| Rollup | Anlamı | Yanlış işlenirse |
|---|---|---|
| `average` | Aralıktaki ortalama | — |
| `latest` | Aralığın son değeri | Ortalama sanılırsa ani sıçramalar kaybolur |
| `summation` | Aralıktaki toplam | **Ortalama sanılırsa değer tamamen yanlış olur** |
| `maximum` | Aralıktaki tepe | Ortalamayla karıştırılırsa kapasite planlaması bozulur |
| `minimum` | Aralıktaki dip | — |
| `none` | Yuvarlama yok (yalnızca real-time) | — |

Örnek: `cpu.ready.summation` bir **milisaniye toplamıdır**, yüzde değildir.
Yüzdeye çevirmek için aralık süresine bölmek gerekir:

```
CPU Ready %  =  (cpu.ready.summation [ms] / (aralık [s] × 1000)) × 100
```

20 saniyelik real-time örnekte bölen `20000`, 5 dakikalık örnekte `300000`'dir.
**Aynı ham değer, farklı aralıkta farklı yüzde demektir.**

> **Kural:** Bu kod tabanında ham sayaç değeri asla doğrudan gösterilmez.
> Her sayaç, rollup tipi ve aralık süresiyle birlikte taşınır; dönüşüm tek bir
> yerde yapılır.

Kaynak: [PerformanceManager API](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.PerformanceManager.html)

## 3. Önceki uygulamanın çapraz kontrolü

Önceki ürünün (`VsphereVimPerformance.cs`) yaptıklarını inceledik. Aşağıdaki
tablo neyi koruduğumuzu ve neyi değiştirdiğimizi kaydeder.

### Doğru yapılmış — koruyoruz

| Karar | Neden doğru |
|---|---|
| `PerformanceManager` / `QueryPerf` kullanımı | Gerçek sayaç yolu. `summary.quickStats` tek başına yetersizdir; CPU Ready, disk gecikmesi gibi teşhis metrikleri orada yoktur. |
| `QueryPerfCounterByLevel` ile sayaç keşfi | Sayaç ID'lerini sabit kodlamak yerine sunucudan öğreniyor. Sürüm farklarına dayanıklı. |
| `rollupType` okunuyor | Metriğin anlamı korunuyor (satır 342, 368). |
| Varlık bazlı batch'leme (`Chunk`) | Tek devasa sorgu yerine parçalı sorgu. vCenter'ı korur. |
| `intervalId = 20` (real-time) | vCenter veritabanını baypas eder, anlık durum için doğru tercih ve DB yükü yaratmaz. |

### Düzeltilecek

| # | Bulgu | Sorun | Bu üründe |
|---|---|---|---|
| 1 | `entityIds.Chunk(32)` — sabit batch boyutu | 32 sayısı sihirli bir sabit. vCenter'ın gerçek sınırı `config.vpxd.stats.maxQueryMetrics` (6.5+ varsayılanı **256**). Müşteri bunu düşürmüşse 32 bile patlar; yükseltmişse veya `-1` yapmışsa gereksiz yere yavaş çalışırız. | Collector başlangıçta `maxQueryMetrics` ayarını **okur**, batch boyutunu ona göre hesaplar, okuyamazsa güvenli varsayılana düşer. Sınır aşımı hatası (`Request processing is restricted by administrator`) açıkça yakalanır ve batch küçültülerek yeniden denenir. |
| 2 | Tüm varlık tipleri için `intervalId = 20` | **Real-time istatistikler her varlık tipinde yoktur.** Datastore ve cluster için real-time besleme bulunmaz; yalnızca tarihsel aralıklar (300 sn+) vardır. Bu varlıklar için `intervalId=20` sessizce boş döner. | Varlık tipi başına aralık seçimi tabloya bağlanır (bkz. §5). Boş dönen sorgu sessizce geçilmez; `Unknown` olarak işaretlenir. |
| 3 | `maxSample = 1` | Tek 20 saniyelik örnek gürültülüdür. 30 saniyelik poll döngüsünde ardışık örnekler arasında boşluk kalır ve kısa sivrilmeler kaçırılır. | Poll aralığını kapsayacak kadar örnek istenir (`maxSample = poll/20`), collector içinde rollup tipine uygun şekilde birleştirilir. |
| 4 | Sayaç seçimi istatistik seviyesine bağlanmamış | Müşteri seviye 1'deyse istenen sayaçların bir kısmı boş döner ve bu fark edilmez. | Bağlantı kurulurken mevcut istatistik seviyesi sorgulanır. İstenen ama seviyesi yetersiz olan sayaçlar **açıkça raporlanır** (Configuration uyarısı) — sessizce eksik veri döndürülmez. README ilke 1. |
| 5 | Ham `summation` değerlerinin dönüşümü dağınık | Aralık süresine bölme mantığı birden fazla yerde. | Tek bir `CounterValue` tipi: ham değer + rollup + aralık süresi. Dönüşüm yalnızca bu tipin üzerinde. |

## 4. Toplama yüzeyleri ve kullanım alanları

| Yüzey | Protokol | Ne için | Maliyet |
|---|---|---|---|
| `PropertyCollector` (`RetrieveProperties`) | vim25 SOAP | Envanter, yapılandırma, `summary.quickStats` | Düşük. Tek çağrıda çok nesne. |
| `PerformanceManager.QueryPerf` | vim25 SOAP | Zaman serisi sayaçlar | Orta–yüksek. Batch boyutuna duyarlı. |
| `PerformanceManager.QueryPerfComposite` | vim25 SOAP | Bir varlık + alt varlıkları tek çağrıda | Düşük. Host + VM'leri için verimli. |
| vCenter REST (`/api/vcenter/...`) | REST/JSON | Basit envanter, oturum | Düşük. Sayaç yok. |
| `WaitForUpdatesEx` | vim25 SOAP | Envanter **değişim** bildirimi | Çok düşük. Poll yerine push. |

**Not:** Önceki ürün envanteri her poll'da baştan çekiyordu. `WaitForUpdatesEx`
ile değişim tabanlı güncelleme, büyük ortamlarda vCenter yükünü önemli ölçüde
düşürür. Bu, ilk dilimin kapsamında değil ama model buna uygun tasarlanmalı —
envanter yenileme ile metrik toplama ayrı ritimler olmalı.

## 5. Varlık tipi başına aralık seçimi

| Varlık | Real-time (20 sn) | Kullanacağımız |
|---|---|---|
| ESXi Host | ✅ var | Real-time |
| Virtual Machine | ✅ var (yalnızca açık VM'ler) | Real-time |
| Resource Pool | ✅ var | Real-time |
| **Cluster** | ❌ yok | Aralık 1 (5 dk) |
| **Datastore** | ❌ yok | Aralık 1 (5 dk) |
| vCenter (kök) | ❌ yok | Aralık 1 (5 dk) |

Kapalı bir VM için real-time veri **yoktur**. Bu bir hata değildir; `Unknown`
olarak modellenir, sıfır olarak değil.

## 6. İlk dilim için metrik listesi (vSphere → alarm → UI)

Ürün vaadi "3 saniye kuralı"dır: operatör ekrana baktığı ilk üç saniyede genel
sıhhati görmeli. İlk dilim bunu karşılayacak asgari kümeyi toplar.

### 6.1 İstatistik seviyesi 2 bir kurulum ön koşuludur

**Karar:** Ürün, vCenter'da **istatistik seviyesi 2** gerektirir ve bunu
kurulum ön koşulu olarak belgeler.

Gerekçe: ürünün teşhis çekirdeği olan disk gecikmesi üçlüsü
(`deviceLatency`, `kernelLatency`, `queueLatency`) seviye 1'de **gelmez**.
Seviye 1 yalnızca temel ortalama sayaçları içerir. Bu üçlü olmadan ürün
"disk yavaş" diyebilir ama "kim yavaş" diyemez — yani rakiplerinden ayrıştığı
tek şeyi yapamaz.

Bu bir tercih değil, ürünün çalışma koşuludur. Alternatifler değerlendirildi:

| Alternatif | Neden değil |
|---|---|
| Seviye 1'le yetin, üçlüyü toplama | Ürünün ana teşhis yeteneği kaybolur |
| Üçlüyü başka yoldan türet | Türetilemez; ESXi bunları ayrı ölçer |
| Sessizce eksik veriyle çalış | README ilke 1'in doğrudan ihlali |

**Uygulama gereği:** Collector bağlantı kurarken mevcut istatistik seviyesini
sorgular. Seviye 2'nin altındaysa:

1. Kurulum/bağlantı testi ekranında **engelleyici olmayan ama görünür** bir
   uyarı verir, yükseltme adımlarını gösterir.
2. Etkilenen metrikler `Unknown` olarak işaretlenir — sıfır veya "sağlıklı"
   değil.
3. Kalıcı bir `Configuration` kategorisinde alarm açılır. Bu alarm, seviye
   yükseltilene kadar açık kalır ve hysteresis'e tabi değildir (durum
   değişmiyor, gürültü üretmiyor).

Seviye değişikliği vCenter tarafında etkili olduktan sonra ilgili sayaçların
dolması **bir sonraki toplama aralığını** bekler (5 dakikaya kadar); collector
bu gecikmeyi hata olarak raporlamaz.

### ESXi Host

| Metrik | Sayaç | Rollup | Neden gerekli |
|---|---|---|---|
| CPU kullanımı | `cpu.usage.average` | average | Doygunluk göstergesi |
| Bellek kullanımı | `mem.usage.average` | average | Doygunluk göstergesi |
| Bellek balon | `mem.vmmemctl.average` | average | **Gerçek bellek baskısı göstergesi.** `mem.usage` yüksekliği tek başına sorun değildir; balon şişmesi sorundur. |
| Bellek swap | `mem.swapused.average` | average | Kritik bellek baskısı |
| **Cihaz gecikmesi** | `disk.deviceLatency.average` | average | **Array/SAN tarafındaki** gerçek hizmet süresi |
| **Çekirdek gecikmesi** | `disk.kernelLatency.average` | average | **VMkernel'de** geçen süre |
| **Kuyruk gecikmesi** | `disk.queueLatency.average` | average | **Kuyrukta bekleme** — queue depth doygunluğu |
| Toplam gecikme | `disk.maxTotalLatency.latest` | latest | Hızlı tarama için özet; tek başına teşhis değeri yok |
| Bağlantı durumu | `runtime.connectionState` (property) | — | Host erişilebilir mi |
| Bakım modu | `runtime.inMaintenanceMode` (property) | — | Alarm bastırma için şart |

> **Disk gecikmesi üçlüsü — ürünün teşhis çekirdeği.**
>
> `maxTotalLatency` bu üçünün toplamıdır ve *hangisinin* yüksek olduğunu
> söylemez. Yani "disk yavaş" der, "kim yavaş" demez — ürünün bitirmeyi vaat
> ettiği "storage suçlama çıkmazı"nın ta kendisi.
>
> | Gözlem | Sonuç |
> |---|---|
> | `deviceLatency` yüksek, `queueLatency` normal | Sorun array veya SAN kumaşında |
> | `queueLatency` yüksek, `deviceLatency` normal | Sorun host tarafında: queue depth, aşırı taahhüt |
> | `kernelLatency` yüksek | VMkernel / sürücü katmanı |
>
> Önceki üründe bu üçlü model seviyesinde **zaten vardı** (`DeviceLatencyMs`,
> `KernelLatencyMs`, `QueueLatencyMs`). Bu belgenin ilk taslağında eksikti;
> çapraz kontrolde fark edildi ve eklendi.
>
> **Sahadan düzeltme (2026-09-19, canlı vCenter 8):** üçlünün tamamının seviye 2
> gerektirdiğini yazmıştım; doğru değil. `deviceLatency` **seviye 1**'de geliyor,
> yalnızca `kernelLatency` ve `queueLatency` seviye 2. Ön koşul hâlâ geçerli ama
> etkisi daha yumuşak: seviye 1'deki bir kurulum "array yavaş" diyebilir,
> "host kuyruğu mu çekirdek mi" diyemez.

### Virtual Machine

| Metrik | Sayaç | Rollup | Neden gerekli |
|---|---|---|---|
| CPU Ready | `cpu.ready.summation` | **summation** | CPU aşırı taahhüdünün tek güvenilir göstergesi. Yüzdeye çevirme §2'deki formülle. |
| CPU eşzamanlılık bekleme | `cpu.costop.summation` | summation | Fazla vCPU atanmış VM tespiti |
| Bellek balon | `mem.vmmemctl.average` | average | VM seviyesinde bellek baskısı |
| Disk gecikmesi | `virtualDisk.totalLatency.average` | average | VM'in gördüğü gerçek depolama gecikmesi |
| Güç durumu | `runtime.powerState` (property) | — | Kapalı VM metriklerini `Unknown` yapmak için |

### Datastore

| Metrik | Kaynak | Neden gerekli |
|---|---|---|
| Kapasite / boş alan | `summary.capacity`, `summary.freeSpace` (property) | Kapasite alarmı |
| Erişilebilirlik | `summary.accessible` (property) | APD/PDL erken uyarısı |
| Gecikme | `datastore.totalLatency.average` (aralık 1) | Array seviyesinde sorun |

### Cluster

| Metrik | Kaynak | Neden gerekli |
|---|---|---|
| HA etkin mi | `configuration.dasConfig.enabled` (property) | Best practice denetimi |
| DRS etkin mi | `configuration.drsConfig.enabled` (property) | Best practice denetimi |
| Toplam/kullanılan kaynak | `summary` (property) | N+1 kapasite hesabı |

> **Önceki üründen alınan ders:** HA/DRS yapılandırması vim25 ile okunamazsa
> cluster satırı host üyeliğinden türetilir ve `ConfigCollected = false`
> işaretlenir; HA/DRS "etkin" olarak **yazılmaz**. Bu davranış doğruydu ve
> korunuyor — README ilke 1'in somut karşılığı.

## 7. Toplamadığımız ve nedeni

| Metrik ailesi | Neden şimdi değil |
|---|---|
| Ağ sayaçları (`net.*`) | İlk dilimin teşhis hedefinde değil. vSwitch/portgroup envanteri ayrı dilim. |
| Konuk içi metrikler (VM Tools) | Ayrı yetki modeli ve güvenilirlik profili. Ayrı ADR gerektirir. |
| vSAN metrikleri | Ayrı API yüzeyi (`vsan-health`). Ayrı collector. |
| Tarihsel geri doldurma | İlk dilim canlı durum odaklı. Trend deposu ayrı dilim. |

---

## Açık sorular

Bu belgede açık soru kalmadı. Kararlar aşağıdaki tabloda.
## Çoklu vCenter: otorite seçmiyoruz, çakışmayı bildiriyoruz

Önceki üründe `VCenterEndpoints` içindeki `IsPrimary` bayrağı bir otorite
kuralı değildi — birden fazla vCenter eklendiğinde otomatik atanıyordu. Yani
bu problem için bilinçli bir kural hiç olmadı.

Yeni üründe de bir otorite kuralı **tanımlamıyoruz**, çünkü problemin kendisi
normal bir durum değil. Bir ESXi host tam olarak bir vCenter'a kayıtlıdır;
Enhanced Linked Mode'da bile her vCenter kendi host'larına sahiptir. Aynı host
iki vCenter'da görünüyorsa üç olasılık var:

| Olasılık | Anlamı |
|---|---|
| Host gerçekten iki vCenter'a kayıtlı | Yanlış yapılandırma. Tehlikeli: iki vCenter aynı host'a komut verebilir. |
| Host taşınmış, eski vCenter'da bayat kayıt kalmış | Temizlik gerektiren durum. |
| Kimlik çözümleme yanlış eşleştirdi | Bizim hatamız; `SameAs` kenarının kanıtı incelenmeli. |

Üçünde de doğru davranış aynı: **bir kaynağı otoriter seçip diğerini sessizce
yok saymak değil, çakışmayı bildirmek.** Keyfi bir "primary" seçmek, ürünün
reddettiği şeyi yapmak olurdu — bilmediğini bilirmiş gibi davranmak
(README ilke 1).

**Uygulama:** Bir `SameAs` denklik bileşeni içinde birden fazla vCenter
kaynaklı host kaydı varsa, `Configuration` kategorisinde bir bulgu üretilir ve
her iki kaynak da kanıtıyla birlikte gösterilir. Sunumda kayıt seçimi
deterministiktir (en son görülen) ama bu bir otorite iddiası değil, yalnızca
kararlı bir görüntüleme tercihidir ve çakışma rozeti ile işaretlenir.

## Kararlaştırılanlar

| Soru | Karar | Nerede |
|---|---|---|
| İstatistik seviyesi yetersizse ne olacak? | Seviye 2 kurulum ön koşulu; eksikse görünür uyarı + kalıcı Configuration alarmı + metrikler `Unknown` | §6.1 |
| Disk gecikmesi hangi sayaçlardan? | Üçlü: `deviceLatency` / `kernelLatency` / `queueLatency`. `maxTotalLatency` yalnızca özet. | §6 ESXi Host |
| Batch boyutu ne olacak? | Sabit yok. `config.vpxd.stats.maxQueryMetrics` çalışma zamanında okunur, sayaç sayısına bölünür, limit hatasında yarılanarak uyarlanır. | ADR-0005 §2 |
| `WaitForUpdatesEx` ne zaman? | İlk dilimde değil. Envanter akışı ilk dilimde `PollingInventorySource`, ikinci dilimde `ChangeFeedInventorySource` — adaptör değişimi, `Application` katmanı değişmez. | ADR-0005 §1 |
| Çoklu vCenter otoritesi? | Otorite seçilmiyor; çakışma `Configuration` bulgusu olarak raporlanıyor. | §Çoklu vCenter |

> **`Chunk(32)` hakkında not:** sabitin kaynağı kayıtlı değil ve artık önemi
> yok — yerine gelen algoritma sunucu limitini kendi öğreniyor. Ancak bir
> gözlem kayda değer: 32 entity × 10 sayaç = 320 metrik, varsayılan 256
> limitinin **üzerinde**. Sınırın metrik sayısına uygulandığı yorum doğruysa,
> o sorgular sahada sessizce başarısız oluyordu.
