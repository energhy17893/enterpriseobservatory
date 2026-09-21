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

## ▶ M3 — Uygunluk motoru *(sürüyor)*

Broadcom SCG CSV'si **veri olarak** yutulur; kod içine kontrol gömülmez.

| Adım | Feature | Bitti = |
|---|---|---|
| M3.1 | SCG CSV içe alma (`Is the Default = NO` süzgeci), sürümlü | kontroller listelenir |
| M3.2 | Bulgu yaşam döngüsü: kabul et, istisna, süre | ekranda kabul/istisna |
| M3.3 | `config.option` ile cevaplanabilen ilk kontroller (syslog, shell timeout, audit) | uygunluk ekranı dolu |
| M3.4 | vSwitch güvenlik politikası, NTP, SSH/Shell servisleri | ekranda bulgu |

## M4 — Kapasite ve eğilim

| Adım | Feature | Bitti = |
|---|---|---|
| M4.1 | Datastore kapasitesi zaman serisi (`disk.capacity/used/provisioned.latest`) | grafik |
| M4.2 | Seri okuma portu (kurallar geçmişi okuyabilsin) | — |
| M4.3 | **Dolma tarihi** — ya "N gün" ya "söyleyemem, sebebi şu" | datastore'da tahmin |
| M4.4 | Aşırı taahhüt bulgusuna tarih | bulguda "X tarihinde dolar" |

**→ Mimari kontrol #2** (M3 + M4 sonrası)

## M5 — Raporlama

Pazar araştırmasında alıcının ilk baktığı şey.

| Adım | Feature | Bitti = |
|---|---|---|
| M5.1 | Alarm/bulgu raporu — CSV ve PDF dışa aktarım | indirilebilir |
| M5.2 | Uygunluk raporu (denetçi formatı) | indirilebilir |
| M5.3 | Kapasite raporu | indirilebilir |
| M5.4 | Zamanlanmış e-posta raporu | posta kutusunda |

## M6 — İkinci satıcı: iLO / iDRAC (Redfish)

| Adım | Feature | Bitti = |
|---|---|---|
| M6.1 | Redfish toplayıcı, firmware alt sınırı + destek matrisi | BMC bağlanır |
| M6.2 | Donanım sağlığı aynı host'a katlanır (çift sayım yok) | host ekranında donanım |
| M6.3 | Sensör kör noktası: okunamayan alt sistem "kapsam boşluğu" | kapsam ekranında |

**→ Mimari kontrol #3**

## M7 — SAN switch ve depolama dizisi

Brocade FOS REST, dizi REST API'leri; dizi volume'ü → datastore eşlemesi.
Detaylandırma M6 bittiğinde.
