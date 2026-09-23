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
