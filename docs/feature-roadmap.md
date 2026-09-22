# Feature yol haritası

Operatörün göreceği yetenekler, sırayla. Her adım bitince işaretlenir; her
yanıtta **hangi adımdayız** buradan okunur. Gerekçeler
`product-architecture.md` §10'da; burada yalnızca ne ve bitti-tanımı var.

**Mimari kontrol** (health, structure, security) her iki kilometre taşında bir,
toplu yapılır — aradaki adımlarda değil.

---

## ✅ M0 — Temel *(tamam, 21 Eylül 2026)*

Envanter, zaman serisi, alarm yaşam döngüsü, 7 analiz kuralı, toplama kapsamı
ekranı, host gelişmiş ayarları, CI'da Postgres, kompozisyon kökü smoke takımı.

---

## ✅ M1 — Teşhis merdivenini tamamla *(tamam, 21 Eylül 2026)*

Verinin çoğu zaten toplanıyor; eksik olan kurallar.

| Adım | Feature | Bitti = |
|---|---|---|
| ✅ M1.1 | **Co-stop ile aşırı vCPU** — "bu VM kullanabildiğinden fazla vCPU'ya sahip" (Broadcom eşiği %3/vCPU) | canlıda alarm/bulgu |
| ✅ M1.2 | **Gerçek bellek baskısı** — swap/sıkıştırma oranı; balon tek başına değil | canlıda alarm |
| ✅ M1.3 | **Düşen paketler** — oran + eşik | canlıda alarm |
| ✅ M1.4 | **Depolama gürültücü komşusu** — yavaş datastore'da en çok IOPS üreten VM'ler (per-VM `virtualDisk` IOPS toplanacak) | canlıda "şu VM'ler bu volume'ü dövüyor" |

## ✅ M2 — Olay akışı *(tamam, 21 Eylül 2026)*

vCenter event stream (`CreateCollectorForEvents`, en yeni sayfa).

| Adım | Feature | Bitti = |
|---|---|---|
| ✅ M2.1 | Olay toplayıcı + depolama *(canlıda; ekranda görsel doğrulama bekliyor)* | olaylar ekranında görünür |
| ✅ M2.2 | HA olayları → alarm (host izole, failover başarısız, master kayıp) | canlıda alarm |
| ✅ M2.3 | NFS/depolama bağlantı kaybı, pNIC flapping | canlıda alarm |
| ✅ M2.4 | Snapshot'ı kimin aldığı (snapshot bulgusuna eklenir) | bulguda kullanıcı adı |

**✅ Mimari kontrol #1** *(tamam: olay okumasına sınır, olay sorgusu düzeltmeleri, vCenter yanıt/DTD/log sertleştirmesi, 13 kural tek IAnalysisRule arayüzünde)*

## ✅ M3 — Uygunluk motoru *(tamam, 21 Eylül 2026 — vSphere 8.0'da 12, VCF 9.1'de 11 kontrol değerlendiriliyor)*

Broadcom SCG CSV'si **veri olarak** yutulur; kod içine kontrol gömülmez.

| Adım | Feature | Bitti = |
|---|---|---|
| ✅ M3.1 | SCG CSV içe alma (`Is the Default = NO` süzgeci), sürümlü *(vSphere 8.0 `803-20260612-01` ve VCF 9.1 `910-20260612-01` `catalogues/scg/` altında, lisansıyla; varsayılan 8.0; ekranda görsel doğrulama bekliyor)* | kontroller listelenir |
| ✅ M3.2 | Bulgu yaşam döngüsü: kabul et, istisna, süre *(şema 6; canlıda doğrulama bekliyor)* | ekranda kabul/istisna |
| ✅ M3.3 | `config.option` ile cevaplanabilen ilk kontroller (syslog, shell timeout, audit) | uygunluk ekranı dolu |
| ✅ M3.4 | vSwitch güvenlik politikası, NTP, SSH/Shell servisleri | ekranda bulgu |

## ✅ M4 — Kapasite ve eğilim *(tamam, 21 Eylül 2026 — ilk tahminler 7 günlük veri birikince)*

| Adım | Feature | Bitti = |
|---|---|---|
| ✅ M4.1 | Datastore kapasitesi zaman serisi *(perf sayacı değil, envanterde zaten okunan `summary.*`: `datastore.{capacity,free,used,uncommitted,provisioned}.bytes`, Latest; 205 seri ≈ 394 MB — bkz. counter map §4; canlıda doğrulama bekliyor)* | grafik |
| ✅ M4.2 | Seri okuma portu (kurallar geçmişi okuyabilsin) | — |
| ✅ M4.3 | **Dolma tarihi** — ya "N gün" ya "söyleyemem, sebebi şu" | datastore'da tahmin |
| ✅ M4.4 | Aşırı taahhüt bulgusuna tarih | bulguda "X tarihinde dolar" |

**✅ Mimari kontrol #2** *(tamam: sessiz kaynağın alarmları korunuyor, yalnızca değişen uygunluk bulguları yazılıyor + geçiş geçmişi, istisna denetim izi, Theil-Sen bellek yükü düşürüldü; şema 7)*

## ✅ M5 — Raporlama *(tamam, 21 Eylül 2026 — PDF yerine yazdırılabilir sayfa, lisans riski yok)*

Pazar araştırmasında alıcının ilk baktığı şey.

| Adım | Feature | Bitti = |
|---|---|---|
| ✅ M5.1 | Alarm/bulgu raporu — CSV ve yazdırılabilir sayfa | indirilebilir |
| ✅ M5.2 | Uygunluk raporu (denetçi formatı) | indirilebilir |
| ✅ M5.3 | Kapasite raporu | indirilebilir |
| ✅ M5.4 | Zamanlanmış e-posta raporu (alarm, uygunluk, kapasite; SMTP ayarı yönetici ekranında) | posta kutusunda |

## Yürütme sırası *(22 Eylül 2026)*

Adımlar **ne**yi, bu bölüm **hangi sırayla ve neden**i söyler. Sıra üç şeye
göre kuruldu: yanlış veri yazan şey önce; aynı dosyaya dokunanlar art arda,
dokunmayanlar paralel; ve geçmişe ihtiyaç duyan iş, o geçmiş temiz birikmeye
başladıktan sonra.

### Kapı: önce ölç, sonra iste

22 Eylül'de canlı vCenter'a karşı ölçüldü: **geçersiz tek bir özellik yolu
(`InvalidProperty`) bütün envanter okumasını düşürüyor** — bir özelliği değil.
`configurationEx.dasConfig` şemaya göre makuldü ve canlıya çıksaydı her
envanter turunu bitirirdi. Bu yüzden:

> Envanter özellik listesine giren her yeni yol ve her yeni vim25 çağrısı,
> birleşmeden önce `probe --from-store --shapes` ile canlıda görülür. PR gövdesi
> çıktıyı (yalnızca ad ve sayı) taşır. Görülmemiş yol birleşmez.

Aynı sebepten yeni yollar **dalga başına tek toplama PR'ında** toplanır: o
liste tek bir dizi, beş PR'ın aynı anda eklediği beş satır beş çakışmadır.
Kurallar ise ayrı dosyalardır ve toplama PR'ından sonra paralel yürür.

### Dalgalar

| Dalga | İçerik | Başlama şartı | Neden burada |
|---|---|---|---|
| **0 — sürüyor** | Kontrol #3 düzeltmeleri; `Fold`'un geç gelen örnekleri yeniden katlaması; `configurationEx` + `path.transport` kablolaması (M8.1, M8.3'ü **sessizlikten çıkarır**, M8.6'yı tamamlar); T0.4b (saat farkı + boşluk sonrası geri doldurma); tek yayım | — | Üçü yanlış ya da eksik veri yazıyor; ikisi main'de duran ama çalışmayan feature. |
| **1** | Paket **A** (okuma bütçesi), **C** (yokluk), **G** (yapılandırma) paralel. Yanında **paket K** (bulgu yaşam döngüsü, [ADR-0024](adr/0024-configuration-checks-are-findings.md)) ve **toplama PR'ı 1**: M8.4 ve M8.7'nin yolları. **K'dan sonra**, doğrudan bulgu olarak: **M8.4** bakım modu engelleri, **M8.7** bitiş tarihi radarı | Dalga 0 yayımı | A, C, G dosya paylaşmıyor. M8.4 tamamen eldeki envanter; M8.7 iki çağrı uzakta ve alıcının ilk sorduğu şeylerden. |
| **2** | Paket **D** (öz-izleme), **B** (katalog + sınıflandırma). **M9.1** build tablosu → **M9.3** destek bitişi → **M9.2** maruziyet (aşağıdaki tanımla). **M10.4** geri kazanım, **M10.5** sağ boyutlandırma | C main'de (D için); dalga 1 yayımı | D bekletilmemeli: servis 21–22 Eylül'de iki kez saatlerce durdu ve ürün bunu söyleyemedi. M9 üçlüsü tek katalog üzerine kurulu, sırayla. M10.4/M10.5 boşluğa duyarsız: eşik ve pencere sayıyorlar, eğim değil. |
| **3** | Paket **E** (sözleşme takımı) → **F** (ortak parçalar). **M8.5** uplink SPOF, **M8.8** yedek tazeliği, **M8.9** VCSA yedeği | A ve B main'de | E, A ile B davranışı değiştirirken yazılırsa iki kez yazılır. M8.5 host başına bir **çağrı** (özellik değil) — maliyeti A'nın ölçümünden sonra bilinir. M8.9 toplayıcıya ikinci bir protokol (REST) sokuyor; F'den önce sokmak F'yi zorlaştırır. |
| **4** | **M10.1** değişim noktası, **M10.2** eşiğe-kalan-süre, **M10.3** temkinli/agresif, **M10.6** what-if | T0.4b yayımından sonra **en az 14 gün kesintisiz** seri | Eğim, boşluklu seride yanlış tarih üretir. 21–22 Eylül'de seride iki büyük boşluk var (5 dk ve ~2,5 saat); ikincisi vCenter'ın 1 saatlik gerçek zamanlı penceresini aştığı için geri doldurulamaz. 14 gün: M4'ün kendi alt sınırının iki katı, haftalık deseni iki kez görmek için. *Benim seçimim; alıntı değil.* |
| **5** | Paket **H** (değişim akışı) → **M6** | E, F main'de | T-P2'nin tamamı M6'nın giriş şartı. A'nın ölçümü "envanter zaman aşımına sığmıyor" derse H dalga 2'ye çekilir. |
| sonra | M10.7 (ADR ister), M9.4–M9.7, M7 | — | — |

**Kritik yol:** dalga 0 → A → E → F → M6. M8, M9 ve M10 bu yolun **yanında**
yürür, üstünde değil: hiçbiri M6'yı bekletmez, M6 da onları.

### Referans ilkeleriyle yeniden kesim *(22 Eylül 2026)*

Beş araştırma kolu (`reference-approaches.md` §10) iş paketlerinin kapsamını
değiştirdi. Tek cümleyle: bulduğumuz açıkların hepsi atlanabilir bir yoldan
doğdu, ve olgun çatılar bunu testle değil **yapıyla** kapatıyor. İki karar
22 Eylül 2026'da **kabul edildi**: [ADR-0025](adr/0025-collectors-only-read.md) (toplayıcı
yalnızca okur) ve [ADR-0026](adr/0026-evaluation-is-three-valued.md) (üç değerli
değerlendirme).

| Paket | Önce | Şimdi | Dayanak |
|---|---|---|---|
| **A — Okuma bütçesi** | batch, limit hatası, varlık yalıtımı, kısmi okuma | **aynı + OTel'in yalıtım kuralı birebir**; limit hatasında metin eşleşmesi birincil kalır (denetimdeki şüphe yanlıştı — KB 301449 metni aynen veriyor) | §10.3 |
| **A′ — Eşzamanlılık** *(yeni, dalga 1)* | T2.3'ün yarısıydı, dalga 3 | tek vCenter'a paralel `QueryPerf` tavanı; başlangıç noktası Telegraf'ın kuralı (**VM / 1500, asla 8'den fazla**) ve canlı gidiş-dönüş ölçümü; "aralık estate boyutuna göre mi" sorusu dahil | A'nın 2000 VM ölçümü: seri okuma 30 sn'ye sığmıyor |
| **T0.4b — Geri doldurma** | "boşluğa göre `maxSample`'ı büyüt" | **yüksek su işareti**: `StartTime` = serinin depodaki en yeni zaman damgası (dışlayıcı), `EndTime` = sunucunun `CurrentTime`'ı; ~1 saate kadar doldurur; saat farkı bedavaya ölçülür. Önce iki ölçüm: `MaxSample` hangi uçtan kesiyor, tek sorguda kaç örnek | §10.3 — Telegraf `endpoint.go`, eksikleriyle |
| **D — Öz-izleme** | `/health`, döngü süresi, bekçi | **çalıştırıcı üretir** (ADR-0025), toplayıcı başına eklenmez; + tutulan oturum sayısı, saat farkı, kuyrukta düşen örnek; + bayat alarm işareti (ADR-0026) | §10.1, §10.2 |
| **E — Sözleşme takımı** | vaka listesi tahmindi | **referansların uzlaştığı yedi vaka + bizden iki** (geçersiz tek özellik yolu; süresi dolan oturumu iki çağrının birlikte fark etmesi); denge kuralı: üretilen = kabul edilen + düşürülen. **İlk toplayıcıdan itibaren zorunlu** | §10.2 |
| **F — Ortak parçalar** | taşıma işi | **yetkinin tersine çevrilmesi** (ADR-0025): `HttpClient`, oturum, temizlik ve öğrenilen durum toplayıcıdan çalıştırıcıya. Büyür; E **önce** | §10.2 |
| **H — Değişim akışı** | dalga 5, M6'nın önünde | **koşullu**: incelenen dört toplayıcının hiçbiri `WaitForUpdatesEx` kullanmıyor; yalnızca ölçüm gerektirirse. M6'nın giriş şartından **çıkar** | §10.3 |
| **Yeni — Depo kuyruğu** *(F ile)* | yoktu | depo kapalıyken örnekler düşüyor ve yalnızca loglanıyor → boyut **ve yaşla** sınırlı bellek kuyruğu, düşen sayılır ve alarm olur; disk taşması ayrı karar | §10.2 — Telegraf + Datadog + Zabbix hybrid |
| **Yeni — Üç değerli kurallar** *(ADR-0026, dalga 2)* | #63 bir örneği kapattı | `IAnalysisRule` sonucu koşul var / yok / bilinmiyor; alarm yeniden başlatmadan önce geri yüklenir | §10.1 |

**M8 ve M9'a etkisi** (§10.4, §10.5):
- **Önce aktar, sonra hesapla.** vCenter'ın zaten hesapladığı ve salt-okunur
  rolün okuyabildiği hükümler yeni kontrol yazılmadan önce yüzeye çıkarılır:
  `configIssue`, donanım sensörleri (`healthSystemRuntime`), `consolidationNeeded`,
  `connectionState`, host profili uyumluluğu (`QueryComplianceStatus` — görev
  çalıştırmadan okunur), datastore bakım modu. **Toplama PR'ı 1'e eklenir.**
- **M8.9 (VCSA yedek durumu) yeniden değerlendirilmeli:** appliance REST uçlarını
  salt-okunur rol büyük olasılıkla okuyamıyor (bir topluluk başlığına göre fiilen
  yönetici; API referansından doğrulanmadı). Önce probe ile ölçülür.
- **Eşikler kaynağıyla gösterilir.** Yaygın sayıların çoğunun birincil kaynağı
  yok (KAVG > 2 ms, DAVG > 20–25 ms 2010 tarihli bir blog tablosu). Resmî üç
  sayı (host CPU %80/%90, ready < %5, depolama 10 ms sürekli) ve Veeam ONE'ın
  yayımlanmış varsayılanları taban alınır; her eşik ekranda kaynağını taşır.
- **Uygulanmamış iki ilke**, M10'dan önce ucuz: en kötü 20 saniyelik örnek
  (veri ve `max` hazır), ve VM başına dört hizmette aşım sayısı → kümeye
  "hizmet alan VM yüzdesi".

### M9.2'nin yeniden tanımı

Yol haritası "build → güvenlik açığı maruziyeti"ni VMSA beslemesinden kurmayı
öngörüyordu ve bir açık soru bırakmıştı: besleme düzeltilmiş build'i taşıyor mu.
**Taşımıyor** — API yalnızca duyuru listesini veriyor, etkilenen ve düzeltilen
sürümler duyurunun web sayfasında
([William Lam](https://williamlam.com/2024/09/quick-tip-api-for-broadcom-security-advisories.html):
*"the details are only in URL, which isn't part of API"*). "Bu build şu CVE'ye
açık" demek ya sayfa kazımayı ya da elle bakılan bir eşleme kataloğunu
gerektirir; ikincisi §11'de reddettiğimiz içerik ekibi yüküdür.

Eşleme gerektirmeyen ve **uydurmayan** bir iddia var:

> Bir düzeltme ilk kez D tarihinde yayımlandıysa, D'den **önce** çıkmış hiçbir
> build o düzeltmeyi içeremez.

Bu mantıksal olarak kesin. Tersi kesin değil — D'den sonra çıkmış bir build
düzeltmeyi içerebilir de içermeyebilir de (başka bir sürüm hattı olabilir) — ve
ürün o yönde **bir şey söylemez**. Bulgu build başınadır:

> *"Bu build 14 Mart'ta çıktı. O tarihten sonra bu ürün için N güvenlik duyurusu
> yayımlandı; M tanesi CISA'nın istismar edildiği bilinen açıklar listesinde."*

Gereken: M9.1'in build → çıkış tarihi tablosu, VMSA listesi (tarih, önem, CVE),
CISA KEV (CVE üzerinden). Kazıma yok, eşleme bakımı yok. **Bilinen sınır:**
duyuru o ürünün o sürüm hattını hiç etkilemiyor olabilir; bulgu bu yüzden
"açıksınız" demez, "gözden geçirin" der ve duyuruların bağlantısını verir.
**Doğrulanacak (dalga 2'nin ilk işi):** VMSA listesinin alan adları — CVE
kimliklerini ve ürün ayrımını taşıyor mu. Taşımıyorsa KEV ile birleşim ürün adı
ve tarih üzerinden kurulur ve M sayısı üst sınır olarak sunulur.

## ▶ T — Toplayıcı temeli *(sürüyor)*

Feature değil, zemin. 21 Eylül 2026 denetimi (üç kol: taşıma/oturum, kaynaklar,
orkestrasyon) çalıştırıcı katmanını sağlam, altındaki vSphere adaptörünü ve
üstündeki durum saklamayı açık buldu. Ortak desen: doğru kurulmuş bir koruma
**yan yoldan atlanıyor**. Her adım **önce hatayı gösteren test**, sonra düzeltme.
T-P0 hemen; T-P1 M8'den önce; **T-P2, M6'nın giriş şartı.**

### ✅ T-P0 — sessizce yanlış veri ya da üretime zarar *(main'de, 21 Eylül 2026 — #47; canlıda doğrulandı)*

| Adım | Açık | Bitti = |
|---|---|---|
| ✅ T0.1 | **Bozuk/kesik envanter yanıtı başarı sayılıyor** — `ParsePage` ayrıştırılamayan XML'de boş sayfa dönüyor, varlıklar ilk kaçırmada "kayboldu" | bozuk yanıt okuma hatası; hiçbir varlık kaybolmaz |
| ✅ T0.2 | **Veritabanı kesintisinde devre kesici donuyor** — sağlık belleği ancak yazım başarılıysa güncelleniyor; kilit yazım boyunca tutuluyor | DB kapalıyken kesici ilerler; kilit DB'yi kapsamaz |
| ✅ T0.3 | **Olay okuması devre kesicinin dışında** — yanlış parolada her turda reddedilmiş Login | olaylar aynı kesiciden geçer |
| ◐ T0.4 | **Yalnızca son örnek saklanıyor** — toplam/arıza sayaçlarında olay kaçıyor, geri doldurma yok, damga yerel saatten *(a: tüm örnekler vCenter damgasıyla — canlıda 30→20 sn, 1,495× satır; kontrol #3 çift sayım düzeltmesi #53'te. **b açık**: saat farkı ölçümü + boşluk sonrası geri doldurma, `Fold` düzeltmesine bağlı)* | dönen tüm örnekler vCenter `sampleInfo` damgasıyla yazılır; saat farkı ölçülür |
| ✅ T0.5 | **vCenter'da bırakılan nesneler** — `Logout` çağrılmıyor, zaman aşımında ContainerView sızıyor, sayfalama token'ı iptal edilmiyor *(Logout canlıda sunucudan doğrulandı: `probe --sessions --confirm-logout`; oturum yarışı #53'te. **Gözle doğrulanmadı**: servis `Ctrl+C` ile kapanırken oturumun düşmesi — salt-okunur hesap oturum listesini okuyamıyor)* | her yolda temizlik; test sayar |

### T-P1 — ölçek ve dayanıklılık

| Adım | Açık | Bitti = |
|---|---|---|
| T1.1 | Okuma ya hep ya hiç; zaman aşımı tek ve sabit (envanter de 25 sn) | kısmi ilerleme korunur; zaman aşımı aralıktan türetilir; **2000 VM probe ile ölçülür** |
| T1.2 | Öğrenilen batch boyutu saklanmıyor (ADR-0005 §2) | boyut oturum boyunca hatırlanır |
| T1.3 | Limit hatası metni *(şüpheli)* — `RestrictedByAdministrator` fault tipi | canlıda doğrulanır; tip üzerinden eşleşir |
| T1.4 | Tek varlığın hatası tüm tipi düşürüyor; probe `moRefs[0]`'ı süzmüyor | hata varlıkla sınırlı |
| T1.5 | Sayaç kataloğu geçersizleşmiyor; boş katalog önbellekte | yeniden girişte tazelenir; boş katalog saklanmaz |
| T1.6 | Sınıflandırma: `PasswordExpiredFault`, sessiz boş perf XML'i | ilki yeniden denenmez; ikincisi hata kaydı bırakır |
| T1.7 | Gözlem kapsamında susan kaynağın alarmları *(doğrulanacak)* | `AlertReconciler` okunur; gerekirse ileri taşıma |
| T1.8 | **Ürün kendi sağlığını ölçmüyor** | `/health` tazeliği yansıtır; döngü süresi, yazılan/düşen örnek, döngü bekçisi; depolama hatası alarm |

### T-P2 — ikinci toplayıcıdan önce tutarlılık *(M6 giriş şartı)*

| Adım | Açık | Bitti = |
|---|---|---|
| T2.1 | **Toplayıcı sözleşme test takımı** — kısmi okuma, boş yanıt, iptal, kimlik reddi, çakışan okuma | vSphere geçer; Redfish geçmeden birleşmez |
| T2.2 | Ortak parçalar vSphere derlemesinden çıkar — batch boyutlandırma, hedef seçimi, kapsam, normalleştirme kuralı, kayıt defteri | Redfish kopyalamadan kullanır |
| T2.3 | Eşzamanlılık kapısı ADR-0005 §5'e uyar — aile başına, pipeline'lar arası ortak | tek vCenter'a istek tavanı |
| T2.4 | Yapılandırma ve başlangıç — `CollectionPolicy` ayarlanabilir, aralık alt sınırı, TLS thumbprint, açılışta DB yeniden denemesi | doğrulanmış ayarlar |
| T2.5 | `WaitForUpdatesEx` (`ChangeFeedInventorySource`) — ADR-0005 "ikinci dilim" | adaptör değişimi, Application değişmez |

### İş paketleri — T-P1 ve T-P2 nasıl paralel yürür

Adımlar açığı tarif eder; paketler **kimin neye dokunacağını**. Aynı pakette
olanlar aynı dosyalara dokunur ve tek PR'dır; farklı paketler dosya paylaşmaz ve
paralel yürür. `→` bağımlılıktır: soldaki main'e girmeden sağdaki başlamaz.

| Paket | Adımlar | Dokunur | Bağımlılık | Neden bu gruplama |
|---|---|---|---|---|
| **A — Okuma bütçesi** | T1.1, T1.2, T1.3, T1.4 | `VsphereObservationSource`, `AdaptiveBatchSizer`, `CollectionPolicy` | — | Dördü de tek döngüde, `ReadTypeAsync`'te buluşuyor: batch boyutu, limit hatası, varlık hatası ve kalan süre aynı `while`'ın kararları. Ayrı PR'lar aynı yirmi satırda çakışır. **Önce ölçüm**: 2000 VM'lik sentetik hedef listesiyle probe. |
| **B — Katalog ve sınıflandırma** | T1.5, T1.6 | `VsphereClient` (katalog önbelleği), `VsphereSoapFault`, `PerfResponseParser` (sessiz boş XML) | #53 → | Üçü de "yanlış cevabı sessizce kabul etme" ailesi. #53 aynı iki dosyaya dokunuyor. |
| **C — Yokluk** | T1.7 | `MonitoringCycle`, `AlertReconciler` | — | Önce **okuma**, sonra karar: yorum bilinçli bir tercih diyor. Sonuç "değişiklik gerekmez" olabilir; o zaman bir testle sabitlenir. |
| **D — Öz-izleme** | T1.8 | `MonitoringWorker`, `MonitoringCycle` (sayaçlar), Api (`/health`), yeni `platform` alarmları | C → | `MonitoringCycle`'ı C ile paylaşır. En yüksek operatör değeri burada: bu hafta servis iki kez saatlerce durdu ve ürün bunu söyleyemedi. |
| **E — Sözleşme takımı** | T2.1 | yeni `tests/…Collectors.Contract` | A, B → | A ve B davranışı değiştirirken sözleşmeyi sabitlemek iki kez yazdırır. Vakalar: kısmi okuma, boş yanıt, iptal, kimlik reddi, çakışan okuma, **geçersiz tek özellik yolu** (22 Eylül ölçümü: `InvalidProperty` tüm okumayı düşürüyor), süresi dolan oturumu iki çağrının birlikte fark etmesi. |
| **F — Ortak parçalar** | T2.2, T2.3 | yeni `Collectors.Shared`; `AdaptiveBatchSizer`, hedef seçimi, kapsam, kayıt defteri taşınır; eşzamanlılık kapısı | E → | Taşıma, sözleşme takımı yeşilken yapılır ki "davranış değişmedi" kanıtlanabilsin. |
| **G — Yapılandırma ve başlangıç** | T2.4 | `Program.cs`, `CollectionPolicy`, `VsphereConnectionOptions` | — | Kimseyle dosya paylaşmaz; boş bir ajana verilebilir. |
| **H — Değişim akışı** | T2.5 | yeni `ChangeFeedInventorySource` | E, F → | En büyük ve en az acil. A'nın ölçümü "envanter 25 sn'ye sığmıyor" derse öne alınır. |

| **K — Bulgu yaşam döngüsü** | [ADR-0024](adr/0024-configuration-checks-are-findings.md) | `Domain/Compliance`, `Application/Compliance`, `PostgresComplianceStore` + şema, `ComplianceApi`, web uygunluk ekranı; M8'in dört kuralı (`Application/Analysis`) | — | A, C, G ile dosya paylaşmaz. **Üç PR, sırayla:** (1) değerlendirici varlık türünden bağımsız + bulgu kimliğine "konu" + ürüne ait `eo-continuity` kataloğu — SCG davranışı mevcut testlerle **değişmeden**; (2) M8.1, M8.3, M8.6 ve N+1 kuralları kontrole çevrilir, açık alarmlar bulguya taşınır (susturma **taşınmaz**); (3) ekran ve raporlar kaynağı ayrı gösterir. M8.4, M8.7 ve M9'un tamamı (2)'den sonra doğrudan bulgu olarak yazılır. |

**Bugün başlayabilecekler:** A, C, G, K (dördü bağımsız). **#53'ten sonra:** B.
**Kritik yol:** A → E → F → M6. D, kritik yolda değil ama bekletilmemeli.

## M8 — Süreklilik duruşu

**Numara kimliktir, sıra değil:** M8, M5'in hemen arkasında ve M6'nın önünde
yürür. Neredeyse tamamı eldeki vim25 verisiyle cevaplanıyor — yeni toplayıcı
değil, yeni sorgu. Kaynaklar ve kovalar `reference-approaches.md` §9'da.
Oradaki vim25 yolları **doğrulanmamıştır**; her adım API referansına karşı
kontrolle başlar.

| Adım | Feature | Bitti = |
|---|---|---|
| ◐ M8.1 | **Küme başına HA karnesi** *(kural ve parser main'de — #48, #49; **sessiz**: `configurationEx` toplanmıyor, kablolama sürüyor)* — admission control, host/VM izleme, APD/PDL yanıtı, heartbeat datastore sayısı, `das.ignoreRedundantNetWarning` ile **gizlenmiş risk** | küme ekranında karne |
| ✅ M8.2 | **N+1 what-if + tarih** *(#52)* — "en büyük host düşerse ayakta kalır mı; bu güvence hangi tarihte kaybolur" (M4 eğilimi yeniden kullanılır) | kümede cevap + tarih |
| ◐ M8.3 | **DRS affinity/anti-affinity ihlali** *(#48; M8.1 ile aynı sebepten **sessiz**)* — kural ↔ VM'in fiilen çalıştığı host | bulgu |
| M8.4 | **Bakım modu / vMotion engelleri** — bağlı ISO, tek host'a bağlı datastore, konsolidasyon bekleyen disk, kapalı EVC | host'ta "bakıma alınamaz, sebebi şu" |
| M8.5 | **Uplink SPOF** — bir team'in iki pNIC'i aynı fiziksel switch'e iniyor (`QueryNetworkHint`, CDP/LLDP; pasif, vDS Health Check tetiklenmez) | bulgu |
| ◐ M8.6 | **Depolama yolu SPOF** — tüm yollar tek HBA'dan ya da tek hedef kontrolcüden geçiyor (`multipathInfo` zaten toplanıyor) *(tek yol + tek HBA: #46, #50. **Açık**: tek hedef port — `path.transport` okunacak; canlıda 1240 FC yolu WWPN taşıyor, 16 SAS + 2 PCIe taşımıyor → `null`, yargılanmaz)* | bulgu |
| M8.7 | **Bitiş tarihi radarı** — ESXi sertifikası (`config.certificate`; yalnızca `notAfter` + parmak izi saklanır), vCenter sertifikası (TLS el sıkışması). **Lisans düştü** *(22 Eylül: `LicenseManager` salt-okunur rolde `NoPermission`; karar A — tek salt-okunur hesap vaadi korunur; vCenter'ın kendi lisans alarmı `triggeredAlarmState` ile aktarılır)* | geri sayım + bulgu |
| M8.8 | **Yedek tazeliği** — yedekleme aracının VM özel niteliğine yazdığı son başarılı yedek ↔ etiket/klasör başına RPO; satıcıdan bağımsız | "N saattir yedeği yok" |
| ✗ M8.9 | **VCSA dosya tabanlı yedek durumu** — **düştü** *(22 Eylül: `/api/appliance/*` salt-okunur rolde 403; karar A — ikinci/yetkili hesap istenmez. Yerine, olay akışında vCenter'ın yedek olayı geliyorsa aktarılır; gelmiyorsa "salt-okunur hesapla okunamıyor" notuyla kapalı)* | — |
| ✅ M8.10 | **Süreklilik raporu** — M5'e dördüncü rapor *(#51)* | indirilebilir |

## M6 — İkinci satıcı: iLO / iDRAC (Redfish)

| Adım | Feature | Bitti = |
|---|---|---|
| M6.1 | Redfish toplayıcı, firmware alt sınırı + destek matrisi | BMC bağlanır |
| M6.2 | Donanım sağlığı aynı host'a katlanır (çift sayım yok) | host ekranında donanım |
| M6.3 | Sensör kör noktası: okunamayan alt sistem "kapsam boşluğu" | kapsam ekranında |
| M6.4 | **Öncü göstergeler** — ECC `CurrentPeriod` sayaçları, `Drive.FailurePredicted` → **risk bayrağı** (arıza tarihi **üretilmez**) | host'ta bayrak |
| M6.5 | **SSD/NVMe ömür sonu tarihi** — `PredictedMediaLifeLeftPercent` üzerinde doğrusal eğilim; "anma dayanıklılığı", arıza değil | sürücüde tarih |
| M6.6 | **Güç/fan yedekliliği kaybı** *(Redfish özellik adları doğrulanacak)* | alarm |
| M6.7 | **Küme içi firmware/driver sapması** — host'lar birbirine karşı (VIB listesi + `FirmwareInventory`); katalog gerektirmez | bulgu |

**→ Mimari kontrol #3**

## M7 — SAN switch ve depolama dizisi

Brocade FOS REST, dizi REST API'leri; dizi volume'ü → datastore eşlemesi.
Detaylandırma M6 bittiğinde. Şimdiden kayıtlı adımlar:

| Adım | Feature | Bitti = |
|---|---|---|
| M7.a | **Optik bozulma yerelleştirme** — CRC/encoding hata oranı + `media-rdp` rx/tx gücü, `remote-media-*` ile bağlantının **iki ucu**; §4'teki "SFP mi kablo mu" | "şu port, şu uç" |
| M7.b | **Slow-drain etki alanı** — F-port FPI durumu → host/dizi → datastore → VM'ler | olayda etkilenen VM listesi |
| M7.c | **Zoning hijyeni** — defined ↔ effective, üyesi login olmamış zone, peer olmayan çok-initiator'lı zone | bulgu |
| M7.d | **Replikasyon RPO gecikmesi** datastore'a eşlenir (ONTAP `lag_time`, PowerStore `last_sync_timestamp`) | datastore'da RPO |
| M7.e | **Dizi ↔ ESXi bağlantı çapraz doğrulaması** — dizinin gördüğü host bağlantı durumu ↔ ESXi yol sayısı | bulgu |
| M7.f | **Uçtan uca zincir SPOF** — HBA WWPN → switch portu → efektif zone → dizi kontrolcüsü | zincirde tek nokta bulgusu |

## M9 — Maruziyet ve yaşam döngüsü

M3'ün yolu: besleme **veri olarak** yutulur, sürümlü, `catalogues/` altında.

| Adım | Feature | Bitti = |
|---|---|---|
| M9.1 | **Build → sürüm tablosu** — kendi derlediğimiz sürümlü dosya (resmî kaynak yalnızca HTML) | host'ta sürüm + çıkış tarihi |
| M9.2 | **Güvenlik açığı maruziyeti** — bu build'in çıkışından **sonra** yayımlanan duyurular + CISA KEV; bulgu **build başına** *(yeniden tanımlandı: VMSA beslemesi düzeltilmiş build'i taşımıyor — bkz. "Yürütme sırası")* | "bu build'den sonra N duyuru, M'si istismar ediliyor" |
| M9.3 | **Genel destek bitişi** — ESXi/vCenter | geri sayım + bulgu |
| M9.4 | **Küme içi sapma** — build, NTP, gelişmiş ayarlar host'lar arasında; §3 "tutarlılık" sütunu | bulgu |
| M9.5 | **Performans best-practice** — host güç politikası (küme başına tek bulgu), NUMA düğümünden geniş VM, limit altında balon, eski adaptörler (E1000/LSI — sayısıyla **tek** bulgu), hot-add yalnızca donanım sürümü < 20 ve geniş VM | bulgu |
| M9.6 | **Tools / donanım sürümü** — bulgu **değil**, envanter görünümü (30–100 kez ateşler) | süzülebilir liste |
| M9.7 | **vSAN sağlık aktarımı** — varsa; `VsanQueryVcClusterHealthSummary` sonucu aktarılır, kontroller yeniden yazılmaz | kümede vSAN sağlığı |

## M10 — Öngörü v2

Tamamı açıklanabilir ve mevcut min/max/sum/count/last kovalarıyla çalışır.

| Adım | Feature | Bitti = |
|---|---|---|
| M10.1 | **Değişim noktası + "geçmişi sıfırla"** — göç sonrası eğim yanlış tarih üretir; M4 tahminleri de bugün buna açık, o yüzden ilk iş | tahmin rejim değişimini söyler |
| M10.2 | **Genelleştirilmiş eşiğe-kalan-süre** — küme CPU/bellek, snapshot büyümesi, VCSA bölüm doluluğu | her birinde tarih ya da "söyleyemem" |
| M10.3 | **Temkinli / agresif tarih yan yana** + `max` üzerinde tepe odaklı varyant | iki tarih, yöntemi yazılı |
| M10.4 | **Geri kazanım** — boşta VM (24 saatin tamamında < 100 MHz, KB 445643), kapalı VM (zamanın %90'ı), eski snapshot | "şu kadar geri kazanılır" |
| M10.5 | **Sağ boyutlandırma** — öneri en çok %50 küçültür, en çok %100 büyütür | VM'de öneri |
| M10.6 | **What-if** — host çıkar, N ortalama VM ekle, donanım yenileme | senaryoda yeni tarih |
| M10.7 | **Haftanın saati taban bantları + gecikme tırmanışı** — günlük veri, Hamed-Rao düzeltmeli Mann-Kendall. **Önce ADR**: `reference-approaches.md` §5'teki "dinamik eşik: şimdilik hiçbiri" kararına dokunuyor | ADR + "üç haftadır tırmanıyor" |

## Bilerek yapılmayacaklar

KB ↔ log eşleştirme (syslog + içerik ekibi ister); sunucu/I/O cihazı HCL hükmü
(resmî API yok); fleet verisiyle eğitilmiş yük skoru; Prophet/k-means gibi elle
doğrulanamayan tahmin; ECC/SMART'tan arıza **tarihi**; varsayılan açık zombie
VMDK taraması; BIOS profil kuralları; vDS Health Check'i tetikleyen her şey.
