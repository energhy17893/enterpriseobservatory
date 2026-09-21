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

## ▶ T — Toplayıcı temeli *(sürüyor)*

Feature değil, zemin. 21 Eylül 2026 denetimi (üç kol: taşıma/oturum, kaynaklar,
orkestrasyon) çalıştırıcı katmanını sağlam, altındaki vSphere adaptörünü ve
üstündeki durum saklamayı açık buldu. Ortak desen: doğru kurulmuş bir koruma
**yan yoldan atlanıyor**. Her adım **önce hatayı gösteren test**, sonra düzeltme.
T-P0 hemen; T-P1 M8'den önce; **T-P2, M6'nın giriş şartı.**

### T-P0 — sessizce yanlış veri ya da üretime zarar

| Adım | Açık | Bitti = |
|---|---|---|
| T0.1 | **Bozuk/kesik envanter yanıtı başarı sayılıyor** — `ParsePage` ayrıştırılamayan XML'de boş sayfa dönüyor, varlıklar ilk kaçırmada "kayboldu" | bozuk yanıt okuma hatası; hiçbir varlık kaybolmaz |
| T0.2 | **Veritabanı kesintisinde devre kesici donuyor** — sağlık belleği ancak yazım başarılıysa güncelleniyor; kilit yazım boyunca tutuluyor | DB kapalıyken kesici ilerler; kilit DB'yi kapsamaz |
| T0.3 | **Olay okuması devre kesicinin dışında** — yanlış parolada her turda reddedilmiş Login | olaylar aynı kesiciden geçer |
| T0.4 | **Yalnızca son örnek saklanıyor** — toplam/arıza sayaçlarında olay kaçıyor, geri doldurma yok, damga yerel saatten | dönen tüm örnekler vCenter `sampleInfo` damgasıyla yazılır; saat farkı ölçülür |
| T0.5 | **vCenter'da bırakılan nesneler** — `Logout` çağrılmıyor, zaman aşımında ContainerView sızıyor, sayfalama token'ı iptal edilmiyor | her yolda temizlik; test sayar |

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

## M8 — Süreklilik duruşu

**Numara kimliktir, sıra değil:** M8, M5'in hemen arkasında ve M6'nın önünde
yürür. Neredeyse tamamı eldeki vim25 verisiyle cevaplanıyor — yeni toplayıcı
değil, yeni sorgu. Kaynaklar ve kovalar `reference-approaches.md` §9'da.
Oradaki vim25 yolları **doğrulanmamıştır**; her adım API referansına karşı
kontrolle başlar.

| Adım | Feature | Bitti = |
|---|---|---|
| M8.1 | **Küme başına HA karnesi** — admission control, host/VM izleme, APD/PDL yanıtı, heartbeat datastore sayısı, `das.ignoreRedundantNetWarning` ile **gizlenmiş risk** | küme ekranında karne |
| M8.2 | **N+1 what-if + tarih** — "en büyük host düşerse ayakta kalır mı; bu güvence hangi tarihte kaybolur" (M4 eğilimi yeniden kullanılır) | kümede cevap + tarih |
| M8.3 | **DRS affinity/anti-affinity ihlali** — kural ↔ VM'in fiilen çalıştığı host | bulgu |
| M8.4 | **Bakım modu / vMotion engelleri** — bağlı ISO, tek host'a bağlı datastore, konsolidasyon bekleyen disk, kapalı EVC | host'ta "bakıma alınamaz, sebebi şu" |
| M8.5 | **Uplink SPOF** — bir team'in iki pNIC'i aynı fiziksel switch'e iniyor (`QueryNetworkHint`, CDP/LLDP; pasif, vDS Health Check tetiklenmez) | bulgu |
| M8.6 | **Depolama yolu SPOF** — tüm yollar tek HBA'dan ya da tek hedef kontrolcüden geçiyor (`multipathInfo` zaten toplanıyor) | bulgu |
| M8.7 | **Bitiş tarihi radarı** — ESXi sertifikası, lisans, vCenter sertifikası (TLS el sıkışması) | geri sayım + bulgu |
| M8.8 | **Yedek tazeliği** — yedekleme aracının VM özel niteliğine yazdığı son başarılı yedek ↔ etiket/klasör başına RPO; satıcıdan bağımsız | "N saattir yedeği yok" |
| M8.9 | **VCSA dosya tabanlı yedek durumu** (REST `/appliance/recovery/backup`) | bulgu |
| M8.10 | **Süreklilik raporu** — M5'e dördüncü rapor | indirilebilir |

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
| M9.2 | **Güvenlik açığı maruziyeti** — VMSA JSON + CISA KEV, CVE üzerinden; bulgu **build başına**, host başına değil *(açık soru: VMSA yanıtı düzeltilmiş build'i taşıyor mu)* | "bu build istismar edilen açığa açık" |
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
