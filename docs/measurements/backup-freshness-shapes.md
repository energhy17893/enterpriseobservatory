# M8.8 yedek tazeliği — canlı şekil ölçümü (22 Eylül 2026)

`probe --from-store --candidates-backup`, salt-okunur servis hesabı, canlı
vCenter (10 host, 145 VM). Her yol **tek başına** okundu: koleksiyon
listesinde tek bir geçersiz yol bütün envanter okumasını düşürür
(InvalidProperty). Özel alan **adları** metadata olarak basıldı; alan
**değerleri** hiç basılmadı — her değer bir iskelete indirgendi (rakam → `9`,
sabit bir satıcı sözlüğü dışındaki kelime → `A`, ardışık maskeli kelimeler
→ `A+`) ve yalnızca sınıf sayıları kaydedildi.

## Karar tablosu (kapı)

| Yol / çağrı | Görüldü | Şekil | Hata | Maliyet | Listeye |
|---|---|---|---|---|---|
| `CustomFieldsManager.field` — `RetrievePropertiesEx`, tek nesne, görünüm yok | 1/1 | yapı: `CustomFieldDef` ×8 (`key`, `name`, `type`, `managedObjectType`, `fieldDefPrivileges` ×5, `fieldInstancePrivileges` ×5) | — | 5,1 KB, 29 ms | **evet**, **ayrı çağrı** (kök klasör alarmları gibi; reddedilirse yalnızca yedek okuması düşer) |
| `VirtualMachine.customValue` | 145/145 | 86 VM'de yapı (`CustomFieldValue` xsi:type `CustomFieldStringValue` ×172; `key`, `value`), 59 VM'de boş dizi (düz, boş değer), yok 0 | — | 50,6 KB, 2 sayfa, 557 ms (ilk okuma; tekrarında ~160 ms) | **evet**, envanter isteğinde, **bütün** |
| `VirtualMachine.value` | 145/145 | `customValue` ile birebir aynı | — | 49,7 KB | hayır — aynı veri |
| `VirtualMachine.summary.customValue` | 145/145 | aynı | — | 51,7 KB | hayır — aynı veri, alt yol |
| `VirtualMachine.availableField` | 145/145 | `CustomFieldDef` ×290 (VM başına 2) | — | 71,7 KB | hayır — tanımlar yönetici üzerinden bir kez okunuyor |
| `VirtualMachine.config.annotation` (Notlar) | 145/145 | değer | — | 22,1 KB | hayır — "backup" geçen not **0** |

Alt yol kuralı: `customValue`, `ExtensibleManagedObject` üzerinde
`CustomFieldValue[]` olarak bildirilmiş; somut eleman tipi
(`CustomFieldStringValue`) polimorfik olduğu için alt yol denenmedi, özellik
**bütün** istendi. `field` de `CustomFieldsManager` üzerinde bildirilmiş bir
dizi; bütün okunuyor. Boş dizi (`customValue` ölçüldü: 59 VM) "okundu, özel
değer yok" demek; yokluk (0 VM) "okunmadı" demek — ikisi ayrı tutulur.

## (a) Tanımlı özel alanlar

| key | managedObjectType | type | ad |
|---|---|---|---|
| 1 | ClusterComputeResource | string | com.vmware.vcenter.cluster.edrs.upgradeHostAdded |
| 2 | ScheduledTask | string | com.vmware.vcIntegrity.customField.scheduledTask.action |
| 3 | ScheduledTask | string | com.vmware.vcIntegrity.customField.scheduledTask.signature |
| 4 | ScheduledTask | string | com.vmware.vcIntegrity.customField.scheduledTask.target |
| **48** | **VirtualMachine** | string | **Last Backup** |
| **49** | **VirtualMachine** | string | **Backup Status** |
| 101 | Folder | string | com.vmware.vcenter.network.folder.externalManaged |
| 135 | HostSystem | string | AutoDeploy.MachineIdentity |

## (b) VM başına alanlar (yalnızca sayı)

| key | ad | VM | boş olmayan |
|---|---|---|---|
| 48 | Last Backup | 86 | 86 |
| 49 | Backup Status | 86 | 86 |

145 VM'den 86'sı iki alanı da taşıyor; 59'u hiçbir özel değer taşımıyor. Başka
bir alanın değerinde "backup" geçmiyor (0 alan).

## (c) Değer biçimi (değer basılmadan)

**`Last Backup` (key 48), 86 değer:**

| İskelet | Sayı | Okuma |
|---|---|---|
| `99.99.9999 99:99:99` | 84 | `dd.MM.yyyy HH:mm:ss`, **gün önce** — 84'ünün ilk parçası 12'den büyük, ikinci parçası hiçbirinde değil |
| `9.99.9999 99:99:99` | 1 | `d.MM.yyyy HH:mm:ss` (aynı biçim, tek haneli gün) |
| `99/99/9999 99:99:99` | 1 | `MM/dd/yyyy HH:mm:ss`, **ay önce** — ikinci parça 12'den büyük (tek, kesin); 24 saat biçimi, AM/PM yok |

ISO 8601: 0. AM/PM: 0. Boş: 0. Değerde saat dilimi/ofset **yok**: yedek
sunucusunun yerel saati. Aynı estate'te iki farklı yerel ayar görüldü (eğik
çizgili tek değer 30 günden eski — büyük olasılıkla eski bir sunucu/ayar).

Yaş dağılımı (nokta gün-önce, eğik çizgi ay-önce, **probe makinesinin yerel
saati — Turkey Standard Time, UTC+03:00** olarak okundu): geleceğe düşen 0,
<24 sa **80**, 24–48 sa **1**, 2–7 g 0, 7–30 g 0, >30 g **5**.

**`Backup Status` (key 49), 86 değer:** tarih yok (ISO, eğik çizgi, nokta,
AM/PM: 0). Şablon (her değerde aynı yerdeki kelimeler):
`Backup Job ID [99999999] Client: [A-A9], Backup Set: [A], Subclient: [A…]`.
Parantez içi parçalar: 0 → iş numarası (12 farklı), 1 → client (1 farklı),
2 → backup set (1 farklı), 3 → subclient (8 farklı). Bilinen durum kelimesi
(Success/Failed…) **yok**: alan adına rağmen sonuç taşımıyor, yalnızca hangi
işin yazdığını söylüyor.

**Ürün:** şablon kelimeleri (Backup Job ID, Client, Backup Set, Subclient)
**Commvault**'un terimleri — Veeam değil. Tahmin (Veeam) yanlıştı; satıcıdan
bağımsız okuma kararı bu yüzden doğru çıktı.

## (d) `source_event` — çalıştırılacak salt-okunur SQL

Değer basmaz; yalnızca sayı, tarih aralığı ve iskelet döner.
`psql -h 127.0.0.1 -U observatory -d observatory -f <dosya>`.

```sql
-- 0. Mesaj şekli: rakam -> 9, sözlük dışı kelime -> A (VM adı, değer maskelenir)
SELECT regexp_replace(
         regexp_replace(message,
           '\m(?!(Changed|custom|field|Last|Backup|Status|Job|ID|Client|Set|Subclient|on|in|from|to)\M)[[:alpha:]]+',
           'A', 'g'),
         '[0-9]', '9', 'g') AS shape,
       count(*)
FROM source_event
WHERE event_class = 'CustomFieldValueChangedEvent' AND message ILIKE '%backup%'
GROUP BY 1 ORDER BY 2 DESC LIMIT 20;

-- 1. Alan başına: kaç olay, kaç VM, hangi aralıkta, VM başına günde kaç kez
WITH e AS (
  SELECT vm_ref, created_at_utc,
         substring(message from 'custom field (.+?) on ') AS field
  FROM source_event
  WHERE event_class = 'CustomFieldValueChangedEvent' AND message ILIKE '%backup%')
SELECT field,
       count(*)                      AS events,
       count(DISTINCT vm_ref)        AS vms,
       min(created_at_utc)           AS first_utc,
       max(created_at_utc)           AS last_utc,
       round(count(*)::numeric / NULLIF(count(DISTINCT vm_ref), 0)
             / GREATEST(extract(epoch FROM max(created_at_utc) - min(created_at_utc)) / 86400, 1)::numeric, 2)
                                     AS per_vm_per_day
FROM e GROUP BY field ORDER BY events DESC;

-- 2. Ne sıklıkla: aynı VM'de ardışık iki 'Last Backup' değişikliği arası (6 saatlik kovalar)
WITH e AS (
  SELECT vm_ref, created_at_utc FROM source_event
  WHERE event_class = 'CustomFieldValueChangedEvent' AND message LIKE '%custom field Last Backup on %'),
g AS (
  SELECT created_at_utc - lag(created_at_utc) OVER (PARTITION BY vm_ref ORDER BY created_at_utc) AS gap FROM e)
SELECT width_bucket(extract(epoch FROM gap) / 3600, 0, 72, 12) AS bucket_6h,   -- 13 = 72 saatten uzun
       count(*)
FROM g WHERE gap IS NOT NULL GROUP BY 1 ORDER BY 1;

-- 3. Saat dilimi: olayın UTC zamanı eksi yeni değerin duvar saati (UTC sayılarak)
--    ~ -3 saat (+ iş süresi) çıkarsa değer UTC+03:00 yerel saattir.
SET TimeZone = 'UTC';
WITH e AS (
  SELECT created_at_utc,
         substring(message from 'to ''([0-9]{1,2}[./][0-9]{1,2}[./][0-9]{4} [0-9]{1,2}:[0-9]{2}:[0-9]{2})''') AS v
  FROM source_event
  WHERE event_class = 'CustomFieldValueChangedEvent' AND message LIKE '%custom field Last Backup on %'),
p AS (
  SELECT created_at_utc,
         CASE WHEN v ~ '^[0-9]{1,2}\.' THEN to_timestamp(v, 'DD.MM.YYYY HH24:MI:SS')
              WHEN v ~ '^[0-9]{1,2}/'  THEN to_timestamp(v, 'MM/DD/YYYY HH24:MI:SS') END AS wall
  FROM e WHERE v IS NOT NULL)
SELECT round(extract(epoch FROM created_at_utc - wall) / 3600.0) AS hours_event_minus_value,
       count(*)
FROM p GROUP BY 1 ORDER BY 1;
```

### Sonuçlar (22 Eylül 2026, üretimde salt-okunur, Ertuğrul çalıştırdı)

Olaylar yalnızca 21 Eylül'den beri tutuluyor; pencere
2026-09-21 15:07Z – 2026-09-22 15:25Z (~24 sa). Yalnızca sayı.

| Sorgu | Sonuç |
|---|---|
| 0 — mesaj şekli | yalnızca iki alan: `Last Backup` (değerlerin **tümü** nokta biçimi `99.99.9999 99:99:99`) ve `Backup Status` (iş şablonu); başka şekil yok |
| 1 — alan başına | `Last Backup` **70 olay / 47 VM**; `Backup Status` **70 / 47**; VM başına günde **1,47** değişiklik |
| 2 — ardışık iki `Last Backup` değişikliği arası (6 sa kovaları) | 0–6 sa: **2**; 18–24 sa: **11**; 24–30 sa: **10**; 30 sa üstü: **0** (23 aralık) |
| 3 — olayın UTC zamanı eksi değerin duvar saati | **−3 sa: 70/70** → değerler UTC+03:00 yerel saat; toplayıcının saat dilimi varsayımı **doğrulandı** |

Okuma: günlük takvim, ama 23 aralığın 10'u 24–30 saat — iş her gece aynı
saatte bitmiyor. RPO sınırı takvimin kendisine konursa sağlıklı VM'ler her
gece Failing'e düşer. 0–6 saatlik 2 aralık aynı gün ikinci bir çalıştırma
(yeniden deneme ya da elle tetikleme) olabilir.

## Tasarım kararları

- **Hangi alan "son yedek":** `managedObjectType` VM ya da boş (her tip) olan
  ve adında hem `backup` hem de `last`/`time`/`date`/`when` geçen alan
  (büyük-küçük harf duyarsız). Burada yalnızca `Last Backup` (48) seçilir;
  `Backup Status` (49) tarih taşımadığı için seçilmez. Satıcı adı aranmaz;
  bulgunun gerekçesi de satıcı adı vermez ("no backup attribute").
- **Ayrıştırma (yalnızca ölçülen biçimler + ISO):** `d.M.yyyy H:mm[:ss]` (gün
  önce); `M/d/yyyy` ya da `d/M/yyyy` + `H:mm[:ss]` yalnızca **kesin** olduğunda
  (bir parça 12'den büyükse ya da iki parça eşitse); ISO 8601. İki parçası da
  ≤12 ve farklı olan eğik çizgili tarih **okunamadı** sayılır — tahmin yok.
  Okunamayan değer → "okunamadı", asla "yedek yok".
- **Saat dilimi:** değerde ofset yok. Toplayıcının yerel saat dilimi
  kullanılır ve hangi dilimin kullanıldığı ayara yazılır; bulgu bunu söyler.
  (d) sorgu 3 bunu doğruladı: 70/70 olayda −3 saat.
- **RPO: 36 saat** — kaynak metni "Product policy, measured: 10 of 23 gaps
  24–30 h (daily schedule), limit = daily + 12 h". Günlük takvim + 12 saat
  pay; ölçülen en uzun aralık 30 saatin altında. Sınırın tam üstü geçer
  (yaş = RPO → Passing). Etiketler (CIS REST) ve VM klasörü (`parent`)
  toplanmıyor → **yalnızca estate geneli varsayılan**, VM başına etiket/klasör
  geçersiz kılması yok.

## Toplayıcının kendi okuması (tam envanter isteği, `customValue` içinde)

Kapının asıl sınavı: yeni yol **tam** envanter isteğinde, diğer bütün
yollarla birlikte. `probe --candidates-backup` sonundaki bölüm:

- envanter okundu, 145 VM, hata **0**; `customValue` kapsamı **145/145**
- `backup.read` 145; son yedek niteliği olan 86; zamana çevrilen **86/86**
  (eğik çizgili tek değer dahil — ay önce, kesin)
- zaman temeli: `collector local time, UTC+03:00` (86)
- 36 saatlik RPO ile (22 Eylül akşamı, üçüncü okuma): içinde **81**, daha eski
  **5** (beşi de 30 günden eski), gelecekte 0

## Bu estate için beklenen (145 VM, son okumaya göre)

| Yargı | Sayı | Neden |
|---|---|---|
| Passing | 81 | son yedek ≤ 36 sa |
| Failing | 5 | beşi de > 30 gün (kapsamdan çıkmış ama niteliği kalmış VM'ler olabilir — bulgu yaşı söyler) |
| NotEvaluated ("no backup attribute") | 59 | hiçbir özel değer yok; "yedeklenmiyor" **denmez** |

Sayılar okuma anına bağlı: gece işi bitmeden önce birkaç VM 24 saati geçer,
ama 36 saatin altında kalır — tam da RPO'nun 36 saat seçilme sebebi.

