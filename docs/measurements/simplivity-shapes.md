# M6.0b — SimpliVity REST şekil ölçümü (şablon, henüz canlı ölçülmedi)

Bu dosya, ikinci vCenter (SimpliVity'nin altında olduğu) eklenip
`RedfishProbe --from-store --kind simplivity` canlı bir OmniStack Virtual
Controller'a karşı çalıştırıldığında doldurulacak şablondur. `--dry --kind
simplivity` çıktısı HPE'nin yayımladığı OmniStack REST şemasından
(`hpe-simplivity-swagger`) inşa edilen örnek belgelere karşı doğrulandı (bkz.
`tests/EnterpriseObservatory.RedfishProbe.Tests/Fixtures/SimpliVity/SIMPLIVITY-SOURCES.md`)
ve ayrıştırıcıların doğruluğunu kanıtlıyor. Aşağıdaki tablo canlı bir
estate'in gerçek değerlerini istiyor; hiçbiri henüz ölçülmedi, ve buraya
sayı **uydurulmadı**.

## 23 Eylül 2026 denemesi: bağlantı kurulamadı

`RedfishProbe --kind simplivity --from-store KibarHolding-KBSVT --shapes --mask`
→ `POST /api/oauth/token` **FAILED**: "The SSL connection could not be
established". Sertifika doğrulaması gevşek (self-signed kabul) olduğu halde
el sıkışma kurulmadı, yani sertifika değil TLS sürümü/şifre takımı ya da ağ.
Aynı gün ebebek'in SVT_Vcenter bağlantısı da aynı hatayı veriyor. Tablo
bu yüzden hâlâ boş; `hypervisor_object_id` ↔ moRef eşleşmesi de
sayılamadı. Sonraki adım: OVC'nin kabul ettiği TLS sürümünü ağ tarafında
görmek (probe'un iç istisnayı yazması yeterli olur).

Yeniden deneme (probe #134, 13:08): TCP 443 **açık** (2 ms). Token isteği
varsayılan TLS ile de, zorunlu TLS 1.2 ile de el sıkışma sırasında **sunucu
tarafından kapatıldı** (`SocketException`: uzak ana bilgisayar bağlantıyı
zorla kapattı). Ağ yolu değil; OVC, Windows'un önerdiği şifre takımlarından
hiçbirini kabul etmiyor gibi. Sonraki adım: OVC'nin kabul ettiği takımları
görmek (ör. `openssl s_client -connect <ovc>:443 -tls1_2` başka bir
makineden ya da OVC yöneticisinden), ardından Windows'ta o takımın açık olup
olmadığı.

**10.9.1.18 sonucu:** o düğüm erişilemez / ortak şifre takımı yok. Bağlantı
aynı gün erişilebilir düğüm 10.5.1.23'e (CN `omnicube-ip1-23`) taşındı.

## Canlı ölçüm, 23 Eylül 2026 13:30 (10.5.1.23, probe #134)

`RedfishProbe --kind simplivity --from-store KibarHolding-KBSVT --mask` ve
`--shapes --mask`. Salt-okunur.

| Konu | Sonuç |
|---|---|
| `REST_API_Version` / `SVTFS_Version` | 1.28 / 6.3.0.98 |
| Token alma | 136 ms, TLS varsayılan ile başarılı |
| Host sayısı + `state` | 26, ALIVE=26 |
| Küme `arbiter_*` / `upgrade_state` | 13 küme; 13'ünde de `arbiter_required/configured/connected = true`; `upgrade_state` boş (13/13) |
| VM `ha_status` | DEGRADED=1, SAFE=499 — **ilk sayfa (500)**, toplam değil |
| Yedek `state` | PROTECTED=497, SAVING=2, QUEUED=1 — **ilk sayfa (500)**, toplam değil |
| `hypervisor_object_id` bir moRef mi | **0/26** çıplak vim25 moRef'e benzemiyor; biçimi maskeli, okunmadı |
| Token iptali | `POST /api/oauth/revoke` → **401** |

Kolektör için bulgular:

- **Sayfalama:** yanıtlarda `count`, `limit`, `offset` var; probe yalnız ilk
  sayfayı okuyor ve `count`'u yazmıyor. VM ve yedek toplamı ölçülmedi.
  Kolektör `offset` ile gezmeli ya da toplam için `count`'u okumalı.
- **Kimlik:** `hosts[]` `hypervisor_object_id`,
  `compute_cluster_hypervisor_object_id`, `hypervisor_management_system(_name)`
  taşıyor; vSphere host'uyla eşleşme biçimi maskesiz bir okumayla
  belirlenmeli (ör. `<vc-uuid>:HostSystem:host-N` gibi bileşik mi).
- **Oturum:** revoke 401 dönüyor, yani token bırakılmıyor (vSphere logout'un
  karşılığı). Kolektör token'ı turlar arasında tekrar kullanmalı, her tur
  yenisini almamalı.
- **Maske:** küme adları `--mask` altında açık yazılıyor (probe düzeltmesi).

### Tüm sayfalarla yeniden sayım (13:50, sayfalama + biçim düzeltmesiyle)

| Konu | Sonuç |
|---|---|
| Host | 26 / `count` 26, ALIVE=26 |
| Küme | 13 / `count` 13 |
| VM | **699** / `count` 699 — `ha_status` DEGRADED=1, SAFE=698 |
| Yedek | **1 533** / `count` 1 533 — PROTECTED=1 526, QUEUED=4, SAVING=3 |
| `hypervisor_object_id` biçimi | `<uuid>:HostSystem:host-N`, 26/26 (N 2, 5 ya da 6 hane) |
| `compute_cluster_hypervisor_object_id` biçimi | `<uuid>:ClusterComputeResource:domain-cN`, 26/26 |
| `hypervisor_management_system` biçimi | IPv4, 26/26 aynı biçim |
| `hypervisor_management_system_name` biçimi | `aaaa99.aaaaa.aaa` (kbvc01 ile aynı biçim), 26/26 |
| Token / revoke | 231 ms / 401 |

**Kimlik katlama:** SimpliVity host'u vSphere host'una
`(vCenter instance UUID, moRef)` çiftiyle bağlanır: kimliğin `:` ile
ayrılmış ilk parçası vCenter'ın `about.instanceUuid`'i, son parçası
`host-N` moRef'i. Tek başına moRef yetmez (iki vCenter aynı moRef'i
kullanabilir); UUID parçası bunu çözer. Değerlerin eşleşme sayısı
kolektörde ölçülür, burada yalnız biçim var.

### Donanım ağacı (`GET /api/hosts/{id}/hardware`), 23 Eylül 2026 20:55Z

`RedfishProbe --kind simplivity --from-store KibarHolding-KBSVT --hardware`
(salt-okunur; yalnız alan adları ve durum kelimeleri). İkincil kaynakların
şeması doğrulandı: `host.{raid_card, battery, accelerator_card,
logical_drives[].drive_sets[].physical_drives[]}`; fiziksel sürücüde
`status`, `health`, `life_remaining`, `percent_rebuilt`, `media_type`,
`additional_status[]`.

| Ölçü | Değer |
|---|---|
| Çağrı süresi | host başına 0,8–1,0 s; 26 host sıralı 19,6 s |
| `status` sözlüğü | renk: `GREEN` (sarı/kırmızı bu estate'te görülmedi) |
| `health` sözlüğü | `HEALTHY` |
| Fiziksel sürücü | 576, hepsi GREEN/HEALTHY, `life_remaining` > %10 |
| drive_set / logical_drive | 82 / 56, hepsi GREEN/HEALTHY; önbellek `Write Back` 56 |
| raid_card / battery | 26 / 26 GREEN; battery health HEALTHY |
| accelerator_card | 18 GREEN, **8 boş `status`** (kartı olmayan host → Unknown, RED değil) |

Kibar için beklenen S4 alarm sayısı: 0. 26 host'luk sıralı okuma 19,6 s —
her 120 s turunda değil, yavaş kademede ya da paralel okunmalı.

### Yedek listesi sayfalaması (`GET /api/backups`), 24 Eylül 2026

`RedfishProbe --kind simplivity --from-store KibarHolding-KBSVT --backup-paging`
(salt-okunur; yalnız sayılar). Belirti: `eo-cont.backup-freshness` her
envanter turunda aynı büyüklükte gruplarla Passing→Failing, 1–2 dk sonra
geri (18:55–21:53Z; tur 60→120 s olunca periyot da ~1→~2 dk). Koleksiyoncunun
sayfalamasıyla (500'lük `limit`/`offset`, `count`'a kadar) beş okuma, art arda:

| Okuma | Satır | Farklı id | Sıra bozulması | Sınırda eşit `created_at` | En yenisi eski VM |
|---|---|---|---|---|---|
| A, B koleksiyoncu (sırasız) | 1481 | B: 1433 | — | — | A↔B: 30 VM farklı |
| D `state=PROTECTED` (sırasız) | 1477 | 1447 | — | — | 30 |
| C `sort=created_at&order=ascending` | 1481 | 1481 | 0 | 513 (04:00) | 0 |
| E asc + `state=PROTECTED` | — | — | — | — | 1 |

Okumalar sırasında oluşturulan yedek: 0 — kayıp liste değişiminden değil,
**varsayılan sıranın istekler arasında kararsız** olmasından. Sıralı okuma da
tam sıra değil (`created_at` eşitleri, E'de 1 VM). En büyük sayfa: `limit=1000`
→ 1000 satır 1,7 s; `limit=2000` → 1481 (tümü) 1,9 s; `limit=5000` → HTTP 400.
Canlı kanıt: en çok çırpınan VM'in en yeni yedeği 2026-09-23 04:00 (17 sa,
Passing) ↔ 2026-09-22 04:00 (41 sa, Failing) arasında gidip geliyor.

**Karar (fix/backup-freshness-flap):** `PageLimit = 2000` sabit (Kibar'ın
1481 yedeği tek sayfa, sayfa sınırı yok); her liste id ile tekilleştirilir;
2000'i aşan yedek listesi `sort=created_at&order=ascending` ve 50 satır
örtüşen sayfalarla okunur; tekil id < `count` → kısmi okuma: o tur yedek
tarihi gönderilmez, `SimpliVity backups` hatası sayılarıyla bildirilir.

## İlk soru (canlıda henüz cevapsız)

23 Eylül'de ölçülen: olay akışında `com.simplivity.event.*` **0** (bkz.
docs/reference-approaches.md §10.7). Bu, ya bu vCenter'da SimpliVity
olmadığı ya da eklentinin olay üretmediği anlamına geliyor — hangisi
olduğu, SimpliVity'nin hangi ikinci vCenter'da yaşadığı netleşip bu probe
o bağlantıya karşı çalıştırılınca cevaplanacak. **Ölçülmedi.**

## Karar tablosu (kapı)

| Konu | Uç nokta | Görüldü | Şekil | Maliyet | Kolektöre |
|---|---|---|---|---|---|
| `REST_API_Version` / `SVTFS_Version` | `GET /api/version` (auth yok) | ölçülmedi | — | — | — |
| Token alma süresi | `POST /api/oauth/token` | ölçülmedi | — | — | — |
| Host sayısı + `state` dağılımı | `GET /api/hosts` | ölçülmedi | — | — | — |
| Küme `arbiter_*` ve `upgrade_state` | `GET /api/omnistack_clusters` | ölçülmedi | — | — | — |
| VM `ha_status` dağılımı | `GET /api/virtual_machines` | ölçülmedi | — | — | — |
| Yedek sayısı + `state` dağılımı | `GET /api/backups` | ölçülmedi | — | — | — |
| `hypervisor_object_id` bir moRef mi | `GET /api/hosts` | ölçülmedi | — | — | — |
| Donanım ağacı şekli (ikincil kaynak) | `GET /api/hosts/{id}/hardware` | ölçülmedi | — | — | — |
| Token iptali | `POST /api/oauth/revoke` | ölçülmedi | — | — | — |

## `ha_status` = `OUT_OF_SCOPE` anlamı

Enum'da var (`UNKNOWN, OUT_OF_SCOPE, NOT_APPLICABLE, DEGRADED, SAFE,
SYNCING, DEFUNCT`), ama canlıda ne zaman döndüğü ve neyi işaret ettiği
(şablon/duraklatılmış VM mi, kapsam dışı bırakılmış mı) **ölçülmedi**.

## Reddedilenler / bilinmeyenler (§10.7–10.8'den taşındı)

- `zeus.sh` kontrol listesi — yayımlanmamış, taklit edilmeyecek.
- Upgrade Manager ön-doğrulama test adları — **bulunamadı**.
- Host isolation response / datastore heartbeat için HPE önerisi —
  **bulunamadı**.
- SimpliVity 5.x API sürüm numarası ve eşzamanlı token sınırı —
  **bulunamadı**.
- `hardware` uç noktası — ikincil kaynak (HPESimpliVity PowerShell modülü,
  Centreon); probe görmeden kural yazılmayacak, yalnızca şekli kaydedilecek.

## Ham çıktı

Canlı bir OVC'ye karşı `RedfishProbe --from-store <name> --kind simplivity`
çalıştırıldığında buraya yapıştırılacak. Şimdilik yalnızca `--dry` çıktısı
var, ve o HPE'nin şemasından inşa edilmiş örneğe karşı — bu tabloyu
doldurmuyor, yalnızca format doğru mu diye kanıtlıyor:

```
$ dotnet run --project tools/EnterpriseObservatory.RedfishProbe -- --dry --kind simplivity
(bkz. RedfishProbe --dry çıktısı; canlı koşu çalıştırılınca gerçek
 federasyonun raporu buraya gelecek)
```
