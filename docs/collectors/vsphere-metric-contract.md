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

### ESXi Host

| Metrik | Sayaç | Rollup | Neden gerekli |
|---|---|---|---|
| CPU kullanımı | `cpu.usage.average` | average | Doygunluk göstergesi |
| Bellek kullanımı | `mem.usage.average` | average | Doygunluk göstergesi |
| Bellek balon | `mem.vmmemctl.average` | average | **Gerçek bellek baskısı göstergesi.** `mem.usage` yüksekliği tek başına sorun değildir; balon şişmesi sorundur. |
| Bellek swap | `mem.swapused.average` | average | Kritik bellek baskısı |
| Disk gecikmesi | `disk.maxTotalLatency.latest` | latest | Depolama sorununun ilk işareti |
| Bağlantı durumu | `runtime.connectionState` (property) | — | Host erişilebilir mi |
| Bakım modu | `runtime.inMaintenanceMode` (property) | — | Alarm bastırma için şart |

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

- İstatistik seviyesi yetersizse kullanıcıya nasıl bildirilecek? Kurulum
  sihirbazında mı, sürekli bir Configuration alarmı olarak mı?
- `WaitForUpdatesEx` ne zaman devreye alınacak — ilk dilimden sonra mı, yoksa
  envanter modeliyle birlikte mi?
- Çoklu vCenter ortamında aynı fiziksel host iki vCenter'da görünürse
  (bağlantılı mod), kimlik çözümleme hangi kaynağı otoriter sayacak?
