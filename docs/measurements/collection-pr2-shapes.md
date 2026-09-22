# Toplama PR'ı 2 — canlı şekil ölçümü (22 Eylül 2026)

`probe --from-store --candidates-pr2`, salt-okunur servis hesabı, canlı
vCenter (10 host, 145 VM, 3 küme, 41 datastore). Her aday **tek başına**
okundu: koleksiyon listesinde tek bir geçersiz yol bütün envanter okumasını
düşürür (InvalidProperty). Çıktı yalnızca ad, tip ve sayı taşır; hiçbir değer
(alarm adı, EVC modu, nesne adı) basılmaz.

## Karar tablosu

| Yol / çağrı | Görüldü | Şekil | Hata | Maliyet | Listeye |
|---|---|---|---|---|---|
| `Folder(rootFolder).triggeredAlarmState` — `RetrievePropertiesEx`, tek nesne, görünüm yok | 1/1 | yapı: `AlarmState` ×4 (`key`, `entity`, `alarm`, `overallStatus`, `time`, `acknowledged`, `eventKey`); `entity` tipi `Folder` ×3, `HostSystem` ×1; red ×2, yellow ×2 | — | 1,7 KB, 31 ms | **evet**, **ayrı çağrı** olarak (envanter isteğine değil) |
| `HostSystem.summary.maxEVCModeKey` | 10/10 | değer | — | 2 KB, 98 ms | **evet**, envanter isteğinde |

Alt yol kuralı: `HostSystem.summary`, somut `HostListSummary` olarak
bildirilmiş (kümenin `summary`'si gibi soyut `ComputeResourceSummary` değil),
ve alt yol canlıda tek başına hatasız okundu — PR 1'de de (10/10). Bu yüzden
`summary` bütün istenmedi.

### Kök klasör alarmları — ölçülenler

- Kökün 4 alarm durumundan **3'ü kök klasörün kendisinde** (`entity` = kök
  klasör): vCenter kapsamlı alarmlar bugüne kadar hiç gelmiyordu.
- Kökteki 1 anahtar toplanan bir nesnede de var (host alarmı, kümede de
  görülüyor — alarmlar ağaçta yukarı yayılıyor); 3 anahtar hiçbir toplanan
  nesnede yok.
- **Toplanan dört tipin anahtarlarından kökte olmayan: 0.** Kök, estate'teki
  her alarmı taşıyor; anahtarla tekilleştirme (bugünkü yol) birleşimi doğru
  tutuyor.

### Tasarım kararı: kök alarmları hangi varlığa bağlanır

Grafta bir vCenter varlığı var (`EntityId.For(instance, "vcenter")`,
`EntityKind.VCenter`); M8.7 `eo-cont.cert-vcenter` bulgusu da ona bağlı. Kök
klasörde (`entity` = `ServiceContent.rootFolder`) yükselen alarm bu varlığa
bağlanır, bugünkü `vcenter-alarm` yolundan geçer (parmak izi = vCenter'ın
alarm anahtarı, `alarmId.entityId`). Kaynak düzeyinde ayrı bir özne
gerekmedi. Kök dışındaki klasör/datacenter alarmları bugünkü gibi
`vcenter-alarm-unattached` altında sayılır.

Kök okuması envanter isteğine ikinci bir `objectSet` olarak **eklenmedi**:
ayrı bir çağrı, reddedilirse yalnızca bu alarmları kaybeder ve hata
(`root folder: triggeredAlarmState`) okuma hatası olarak kaydedilir; envanter
isteğinde tek bir hata bütün okumayı düşürürdü.

### EVC — bu estate'te beklenen hükümler

| Küme | EVC | Host | `maxEVCModeKey` okunan | Farklı mod | `eo-cont.maint-evc` |
|---|---|---|---|---|---|
| #1 | kapalı | 2 | 2 | 1 | **Passing** — "EVC off; all 2 hosts the same CPU generation (…)" |
| #2 | kapalı | 6 | 6 | 1 | **Passing** — "EVC off; all 6 hosts the same CPU generation (…)" |
| #3 | açık | 2 | 2 | 1 | **Passing** — "EVC on (…)" |

Bugüne kadar #1 ve #2 NotEvaluated'dı ("maxEVCModeKey is not collected").

## Ham çıktı (`--candidates-pr2`)

```
=== Collection PR 2 candidates (each read alone; names and counts only) ===

  Folder.triggeredAlarmState                         SEEN   objects 1, value 0 (non-empty 0), structure 1, missing 0 , absent 0
                                                            pages 1, reply 1690 chars, 31 ms
      AlarmState <AlarmState> x4
        acknowledged x4
        alarm <Alarm> x4
        entity <Folder> x3
        entity <HostSystem> x1
        eventKey x4
        key x4
        overallStatus x4
        time x4
  root alarm states              4
  by entity type                 Folder(3), HostSystem(1)
  by overallStatus               red(2), yellow(2)
  raised on the root folder itself   3
  with key, entity and alarm         4
  distinct keys                      4
  HostSystem               triggeredAlarmState  objects 10, alarm states 1
  VirtualMachine           triggeredAlarmState  objects 145, alarm states 0
  ClusterComputeResource   triggeredAlarmState  objects 3, alarm states 1
  Datastore                triggeredAlarmState  objects 41, alarm states 0
  root keys also on a collected object   1
  root keys on no collected object       3
  collected keys missing from the root   0

  HostSystem.summary.maxEVCModeKey                   SEEN   objects 10, value 10 (non-empty 10), structure 0, missing 0 , absent 0
                                                            pages 1, reply 2021 chars, 98 ms
  cluster #1   EVC off   hosts 2, maxEVCModeKey read 2, distinct 1
  cluster #2   EVC off   hosts 6, maxEVCModeKey read 6, distinct 1
  cluster #3   EVC on    hosts 2, maxEVCModeKey read 2, distinct 1
  hosts outside a cluster        0
```

## Değişiklikten sonra — tam envanter okuması

`probe --from-store --shapes` (yeni istek listesiyle tek `RetrievePropertiesEx`,
2 sayfa): **hatasız tamamlandı**, hiçbir `MISSING` satırı yok. Yeni yolun
satırı ve verdict anahtarı:

```
  HostSystem (10)
    summary.maxEVCModeKey                        value                  10/10
  verdicts
    HostSystem               evc.maxModeKey                     10/10
    ClusterComputeResource   evc.enabled                        3/3
    ClusterComputeResource   evc.modeKey                        1/3
```

`probe --from-store --mask` (üretimdeki `RetrieveInventoryAsync`, kök çağrısı
dahil): 10 host, 145 VM, 3 küme, 41 datastore, 2 sayfa; okunamayan özellik
satırı **yok** (kök çağrısı hata kaydetmedi). Alarmlar:

```
=== Alarms vCenter has raised ===
  triggered                4   (de-duplicated across the tree)
    red    on HostSystem
    red    on Folder (root)
    yellow on Folder (root)
    yellow on Folder (root)
```

Önceden bu bölüm 1 alarm gösteriyordu (yalnız host alarmı); kök klasördeki 3
alarm artık vCenter varlığına bağlanıyor. (Alarm adları ve nesne referansları
burada bilerek yazılmadı.)
