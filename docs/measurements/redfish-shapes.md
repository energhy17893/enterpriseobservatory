# M6.0b — iLO Redfish şekil ölçümü

**23 Eylül 2026, Kibar Holding, tek iLO (`KibarHolding-alhcesx04-ilo`), canlı.**
`RedfishProbe --kind redfish --from-store … --shapes --mask`. Probe yalnız
alan adı, tür ve sayı yazar, değer yazmaz; iki seviyeden derini (ör.
`Oem.Hpe` altındaki 40 alan) listelemez. Bir cihaz, bir örnek: dağılım
değil, şekil kanıtı. Aşağıdaki "ölçülmedi" satırları hâlâ ölçülmedi.

Bu dosya, ilk iLO kimlik bilgisi girilip
`RedfishProbe --from-store --kind redfish` canlı bir iLO 5/6'ya karşı
çalıştırıldığında doldurulacak şablondur. `--dry --kind redfish` çıktısı
DMTF'nin genel örnek belgelerine karşı doğrulandı (bkz.
`tests/EnterpriseObservatory.RedfishProbe.Tests/Fixtures/Redfish/DMTF-SOURCES.md`)
ve ayrıştırıcıların doğruluğunu kanıtlıyor; aşağıdaki tablo canlı bir cihazın
gerçek değerlerini istiyor, ki hiçbiri henüz ölçülmedi. Buraya sayı
**uydurulmadı** — her satır "ölçülmedi" diyor.

## Karar tablosu (kapı)

| Konu | Yol | Görüldü | Şekil | Maliyet | Kolektöre |
|---|---|---|---|---|---|
| Nesil/firmware | `Managers/1.FirmwareVersion` | var (string) | değer maskeli, nesil ölçülmedi | — | — |
| Power Redundancy[] | `Chassis/1/Power` | var, 1 küme | `Mode`, `MinNumNeeded`, `MaxNumSupported`, `RedundancySet`×2, `Status` | — | aday |
| Thermal Redundancy[] | `Chassis/1/Thermal` | **yok** | Thermal'da `Redundancy` alanı dönmedi | — | fan başına `Status` ile |
| PSU sayısı + durum | `Chassis/1/Power.PowerSupplies[]` | 2 | `Status`, `LastPowerOutputWatts`, `LineInputVoltage`, `SerialNumber` | — | aday |
| Fan sayısı + durum | `Chassis/1/Thermal.Fans[]` | 6 | `Reading`+`ReadingUnits`, `Status`; ayrıca `Temperatures[]` 77 sensör, `UpperThresholdCritical/Fatal` ile | — | aday |
| Sürücü sayısı + FailurePredicted dağılımı | `Systems/1/Storage/*/Drives/*` | **ölçülmedi** | `Storage/1` → HTTP 404; kimlik sabit değil, koleksiyondan gezilmeli | — | probe düzeltmesi |
| DIMM sayısı | `Systems/1/Memory/*` | ölçülmedi | yalnız bağlantı; `MemorySummary.TotalSystemMemoryGiB` var | — | — |
| Firmware envanteri satır sayısı | `UpdateService/FirmwareInventory/*` | 36 | `Members@odata.count` | — | — |
| IML girdi sayısı + en yeni `Created` | `Systems/1/LogServices/IML/Entries` | 207 | en yeni `Created` ölçülmedi | — | — |
| `AggregateHealthStatus` var/yok | `Systems/1.Oem.Hpe.AggregateHealthStatus` | ölçülmedi | `Oem.Hpe` 40 alan, probe derinliği yetmedi; standart `Status.Health/HealthRollup` var | — | — |
| `AgentlessManagementService` değeri | `Systems/1.Oem.Hpe.AggregateHealthStatus.AgentlessManagementService` | ölçülmedi | — | — | — |
| `Systems/1.SerialNumber/UUID` ↔ vim25 `hardware.systemInfo` eşleşme sayısı | (çapraz kaynak) | alanlar iki tarafta da var | değerler maskeli, eşleşme sayılmadı | — | — |
| Uç nokta başına yanıt süresi | (hepsi) | ölçülmedi | — | — | — |

## Nesil/sürüm dağılımı

Estate'teki iLO nesli (5 mi 6 mı) ve firmware sürümü dağılımı: **ölçülmedi**.

## vim25 ↔ Redfish kimlik eşleşmesi

`VsphereProbe --host-identity` çıktısındaki `hardware.systemInfo.{vendor,
model,serialNumber,uuid}` ile bu dosyanın `Systems/1.{SerialNumber,UUID}`
sütunlarının kaç host için eşleştiği: **ölçülmedi** (tek iLO, değerler maskeli).

vim25 tarafı, aynı gün, `KibarHolding-KBVc01`, 59 host:
`hardware.systemInfo.uuid` **59/59**, `serialNumber` **33/59**,
`otherIdentifyingInfo` 426 girdi (host başına ~7), `qualifiedName` 57/59,
`vvolHostId` 35/59. Okuma 134 ms, 130 621 karakter. Seri numarası 26 hostta
boş olduğundan birincil anahtar UUID, seri ikincil
(docs/reference-approaches.md).

## Reddedilenler / bilinmeyenler (§10.7'den taşındı)

- iLO maksimum oturum sayısı ve rate limit — **bulunamadı**, ölçülmedi.
- "GET için LoginPriv yeter" açık cümlesi — **bulunamadı**.
- `MemoryMetrics.CurrentPeriod` ECC sayacı — iLO 6 referansı alanı
  listelemiyor; ölçülmeden kural yazılmayacak.

## Ham çıktı

Canlı bir iLO'ya karşı `RedfishProbe --from-store <name> --kind redfish`
çalıştırıldığında buraya yapıştırılacak. Şimdilik yalnızca `--dry` çıktısı
var, ve o DMTF örneğine karşı — bu tabloyu doldurmuyor, yalnızca format
doğru mu diye kanıtlıyor:

```
$ dotnet run --project tools/EnterpriseObservatory.RedfishProbe -- --dry --kind redfish --mask
(bkz. RedfishProbe --dry çıktısı; docs/measurements/ içine canlı koşu
 çalıştırılınca gerçek host'un raporu buraya gelecek)
```

## Yeniden ölçüm, 23 Eylül 2026 13:10 (probe #134 ile)

Aynı iLO, düzeltilmiş probe (depolama koleksiyondan gezilir, `Oem.Hpe`
derinliği 6, nesil maskesiz). Yukarıdaki tablonun "ölçülmedi" satırlarının
cevabı:

| Konu | Sonuç |
|---|---|
| Nesil / firmware | **iLO 5**, `iLO 5 v3.18` |
| Power / Thermal `Redundancy[]` | Power var, Thermal **yok** |
| PSU / fan | 2 (OK=2) / 6 (OK=6) |
| Depolama | 2 denetleyici (`DE07C000`, `DE082000` — `1` değil), 14 sürücü, Health OK=14, `FailurePredicted=true` 0, `Oem.Hpe.WearStatus` 0/14 |
| DIMM | 24, `Oem.Hpe.DIMMStatus` 24/24 |
| Firmware envanteri | 36 |
| IML | 207 girdi, en yeni `Created` 2026-06-09, `Oem.Hpe.Severity` 207/207, `Repaired` 153/207 |
| `AggregateHealthStatus` | **iLO 5'te de var** (13 alan: `AggregateServerHealth`, `FanRedundancy`, `PowerSupplyRedundancy`, `BiosOrHardwareHealth`, `Memory`, `Network` …); probe başlığındaki "iLO 6 only" yanlış |
| `AgentlessManagementService` | `Ready` |
| Seri / UUID | ikisi de var (maskeli); vim25 eşleşmesi tek hostta sayılmadı |
| Uç nokta süresi | tek çağrı 0,32–0,82 s, IML 2,6 s; tam okuma ~50 çağrı, sıralı **~25 s / iLO** |

Kolektör için sonuç: `AggregateHealthStatus` tek çağrıda (`Systems/1`) sunucu,
fan, PSU yedekliliği, bellek ve ağ özetini veriyor; DIMM/sürücü başına gezinti
(~40 çağrı) yalnız özet sağlıksızken gerekir. 59 host × 25 s sıralı okuma bir
envanter aralığına sığmaz — özet-önce okuma ya da host başına paralellik
kararı kolektör tasarımında.
