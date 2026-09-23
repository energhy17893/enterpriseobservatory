# M6.0b — iLO Redfish şekil ölçümü (şablon, henüz canlı ölçülmedi)

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
| Nesil/firmware | `Managers/1.FirmwareVersion` | ölçülmedi | — | — | — |
| Power Redundancy[] | `Chassis/1/Power` | ölçülmedi | — | — | — |
| Thermal Redundancy[] | `Chassis/1/Thermal` | ölçülmedi | — | — | — |
| PSU sayısı + durum | `Chassis/1/Power.PowerSupplies[]` | ölçülmedi | — | — | — |
| Fan sayısı + durum | `Chassis/1/Thermal.Fans[]` | ölçülmedi | — | — | — |
| Sürücü sayısı + FailurePredicted dağılımı | `Systems/1/Storage/*/Drives/*` | ölçülmedi | — | — | — |
| DIMM sayısı | `Systems/1/Memory/*` | ölçülmedi | — | — | — |
| Firmware envanteri satır sayısı | `UpdateService/FirmwareInventory/*` | ölçülmedi | — | — | — |
| IML girdi sayısı + en yeni `Created` | `Systems/1/LogServices/IML/Entries` | ölçülmedi | — | — | — |
| `AggregateHealthStatus` var/yok | `Systems/1.Oem.Hpe.AggregateHealthStatus` | ölçülmedi | — | — | — |
| `AgentlessManagementService` değeri | `Systems/1.Oem.Hpe.AggregateHealthStatus.AgentlessManagementService` | ölçülmedi | — | — | — |
| `Systems/1.SerialNumber/UUID` ↔ vim25 `hardware.systemInfo` eşleşme sayısı | (çapraz kaynak) | ölçülmedi | — | — | — |
| Uç nokta başına yanıt süresi | (hepsi) | ölçülmedi | — | — | — |

## Nesil/sürüm dağılımı

Estate'teki iLO nesli (5 mi 6 mı) ve firmware sürümü dağılımı: **ölçülmedi**.

## vim25 ↔ Redfish kimlik eşleşmesi

`VsphereProbe --host-identity` çıktısındaki `hardware.systemInfo.{vendor,
model,serialNumber,uuid}` ile bu dosyanın `Systems/1.{SerialNumber,UUID}`
sütunlarının kaç host için eşleştiği (10 hosttan kaçı): **ölçülmedi**.

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
