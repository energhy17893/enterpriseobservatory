# vSphere Sayaç Haritası

Bu belge, **gerçek bir vSphere 8 vCenter'ından ölçülerek** çıkarılmıştır. İçindeki
hiçbir satır hatırlanarak veya belgelerden kopyalanarak yazılmadı.

Ölçüm aracı: `tools/EnterpriseObservatory.VsphereProbe --map`
Ölçüm tarihi: 2026-09-20
Estate: 10 ESXi host, 145 VM, 41 datastore (29'u paylaşımlı SAN), 3 cluster

> [vsphere-metric-contract.md](vsphere-metric-contract.md) **neyi** topladığımızı
> ve **neden** topladığımızı söyler. Bu belge **nereden** toplanabileceğini
> söyler. İkisi farklı sorular ve ikincisini yanlış cevaplamak, birincisini
> doğru cevaplamış olmanızı işe yaramaz hâle getirir.

---

## 0. Bu belge neden var

Ürün, datastore gecikmesini aylarca hiç ölçmedi. Kırk bir datastore — yirmi
dokuzu üretimdeki paylaşımlı SAN volume'ü — sıfır ölçümle duruyordu ve ekranda
hiçbir şey bunu söylemiyordu.

Sebep bulunana kadar **dört ayrı düzeltme** denendi ve dördü de makuldü:

1. Sayaç adı yanlıştı (`datastore.totalLatency.average` diye bir şey yok)
2. Geçmiş aralık sorgusunda zaman penceresi eksikti
3. Uygunluk sorgusunda da eksikti
4. Uygunluk sondajı sessizce boş dönüyordu ve buna güveniliyordu

Hepsi gerçek kusurdu, hepsi düzeltildi, ve **hiçbiri sorunu çözmedi.** Asıl
sebep bunların hiçbiri değildi:

> `datastore.*` gecikme sayaçları **Datastore nesnesinde toplanmaz.**
> **Host** üzerinde toplanır ve instance'ı datastore'un VMFS UUID'sidir.

Datastore'a sormak, isim ne kadar doğru, aralık ne kadar doğru, pencere ne kadar
doğru olursa olsun asla çalışamazdı. Ve yanlış olduğunda hata vermiyordu —
**boş bir performans cevabı, hata değildir.** Boş cevap, "bu datastore boşta"
ile birebir aynı görünür.

Kaybedilen şey bir gün değil, bir alışkanlıktı: **sayaç adını biliyor olmak,
onu nereden isteyeceğinizi bildiğiniz anlamına gelmiyor.**

---

## 1. Temel kural: sayaç adı, nesneyi söylemez

vim25'te bir sayacın **adı** neyi tarif ettiğini söyler; **hangi nesnede
toplandığını** söylemez. `datastore.totalReadLatency.average` adındaki sayaç
Datastore nesnesinde yoktur.

Bu estate'te ölçülen dağılım:

| Grup | ClusterComputeResource | HostSystem | VirtualMachine | Datastore |
|---|---|---|---|---|
| `cpu` | 4 | 15 | 18 | — |
| `mem` | 14 | 45 | 28 | — |
| `disk` | — | 26 | 13 | 10 |
| `datastore` | — | **24** | 7 | **6** |
| `net` | — | 16 | 15 | — |
| `virtualDisk` | — | — | **19** | — |
| `storageAdapter` | — | **9** | — | — |
| `storagePath` | — | **10** | — | — |
| `rescpu` | — | 17 | 17 | — |
| `sys` | — | 23 | 3 | — |
| `power` | 1 | 6 | 3 | — |
| `clusterServices` | 4 | — | — | — |
| `gpu` | 6 | — | — | — |
| `hbr` | — | 5 | — | — |
| `vmop` | 20 | — | — | — |

Hiçbir nesnede toplanmayan gruplar (tanımlı ama bu kurulumda veri yok):
`lwd`, `managementAgent`, `nfs`, `pmem`, `vcDebugInfo`, `vcResources`,
`vflashModule`, `vmotion`, `vmx`, `vsanDomObj`, `vvol`.

`datastore` satırına dikkat: grup **üç ayrı nesnede** var ve **farklı
altkümelerle**. Host 24, VM 7, Datastore 6. Gecikme yalnızca ilk ikisinde.

---

## 2. Nesne başına özet

| Nesne | Aralık | Besleme | Sunulan seri | Ayrık sayaç |
|---|---|---|---|---|
| HostSystem | 20s | gerçek zamanlı | **5526** | 196 |
| VirtualMachine | 20s | gerçek zamanlı | 252 | 123 |
| Datastore | 300s | vCenter veritabanı | 63 | 16 |
| ClusterComputeResource | 300s | vCenter veritabanı | 55 | 49 |

**Seri sayısı ile sayaç sayısı arasındaki fark, instance'lardır.** Host'ta 196
sayaç var ama 5526 seri: her sayaç, cihaz başına bir kez tekrarlanıyor.

> **Sayaç bir seri değildir.** `disk.deviceLatency.average` tek bir sayaçtır;
> 32 LUN'lu bir host'ta 32 seridir. Yalnızca toplamı okumak, "bir LUN yavaş"
> ifadesini "depolama hafif yavaş"a çevirir — ki bu, sahada aranan tek cümlenin
> kaybolması demektir.

Gerçek zamanlı besleme (20s) vCenter veritabanına uğramaz, dolayısıyla ona
maliyeti yoktur. Datastore ve cluster'ın gerçek zamanlı beslemesi **yoktur**;
istemek sessizce boş döner.

---

## 3. Depolama zinciri — ürünün asıl teşhis ekseni

Bu tablo, bir "VM yavaş" şikâyetinin hangi katmanlarda izlenebileceğini gösterir.
Her satır ölçülmüştür.

| Katman | Sayaç | Nesne | Instance ne tanımlar | Adet | Seviye |
|---|---|---|---|---|---|
| Sanal disk | `virtualDisk.totalReadLatency.average` | VirtualMachine | vDisk | — | 1 |
| | `virtualDisk.totalWriteLatency.average` | VirtualMachine | vDisk | — | 1 |
| Datastore (mantıksal) | `datastore.totalReadLatency.average` | **HostSystem** | **VMFS UUID** | 30 | 1 |
| | `datastore.totalWriteLatency.average` | **HostSystem** | **VMFS UUID** | 30 | 1 |
| | `datastore.sizeNormalizedDatastoreLatency.average` | HostSystem | VMFS UUID | 30 | 1 |
| | `datastore.datastoreVMObservedLatency.latest` | HostSystem | VMFS UUID | 30 | 1 |
| LUN / cihaz | `disk.deviceLatency.average` | HostSystem | `naa.*` | 32 | 1 |
| | `disk.kernelLatency.average` | HostSystem | `naa.*` | 32 | 2 |
| | `disk.queueLatency.average` | HostSystem | `naa.*` | 32 | 2 |
| HBA | `storageAdapter.totalReadLatency.average` | HostSystem | `vmhbaN` | 5 | 2 |
| | `storageAdapter.totalWriteLatency.average` | HostSystem | `vmhbaN` | 5 | 2 |
| Yol (fabric) | `storagePath.totalReadLatency.average` | HostSystem | initiator:target:LUN | **126** | 3 |

### Yol instance'ının biçimi

```
fc.51402ec001c7badb:51402ec001c7bada-fc.524a937a22590a19:524a937a22590a19-naa.624a93701b3645f0b94b4abf00012b93
   └── initiator WWNN:WWPN ──┘   └── target WWNN:WWPN ────┘   └── LUN ─────┘
```

Bu, ürünün olay gruplamasının aradığı şeyin ta kendisi: bir SFP arızası tek bir
`storagePath` instance'ını etkiler, tüm HBA'yı değil. Ama **seviye 3** gerektirir
— bu estate'te varsayılan seviyede toplanmıyor.

### VM tarafı

VirtualMachine nesnesi de `datastore.totalReadLatency.average` sunar, **tek
instance ile**: VM'in üzerinde durduğu datastore'un VMFS UUID'si. Yani "bu VM'in
datastore'u yavaş" sorusu VM'den de cevaplanabilir.

---

## 4. Datastore nesnesinin sunduğu 16 sayaç

Gecikme yok. Ama gözden kaçırılmaması gereken şeyler var:

| Sayaç | Birim | Seviye | Instance | Ne işe yarar |
|---|---|---|---|---|
| `disk.capacity.latest` | kiloBytes | 1 | toplam | **Datastore kapasitesi** |
| `disk.used.latest` | kiloBytes | 1 | 18 (VM başına) + toplam | **Kullanılan alan** |
| `disk.provisioned.latest` | kiloBytes | 1 | 14 (VM başına) + toplam | **Sağlanan alan (thin overcommit)** |
| `disk.unshared.latest` | kiloBytes | 1 | 14 (VM başına) | Paylaşılmayan alan |
| `datastore.numberReadAveraged.average` | number | 1 | toplam | Okuma IOPS |
| `datastore.numberWriteAveraged.average` | number | 1 | toplam | Yazma IOPS |
| `datastore.read.average` / `write.average` | KB/s | 2 | 1 + toplam | Verimlilik |
| `datastore.busResets.summation` | number | 2 | 1 | Bus reset |
| `datastore.commandsAborted.summation` | number | 2 | 1 | İptal edilen komut |

> **Bunların hiçbiri zaman serisi olarak toplanmıyor.** Üçü de seviye 1 — yani
> her kurulumda, hiçbir ayar değişikliği olmadan hazır.
>
> `disk.provisioned.latest` ile `disk.capacity.latest` arasındaki fark, thin
> provisioning aşırı taahhüdüdür: datastore dolmadan çok önce haber veren tek
> ölçü.

### Envanterde zaten okunan, sonra atılan veri

Bu, sayaçlardan ayrı ve daha rahatsız edici bir bulgu. Envanter toplayıcısı
Datastore için şunları **zaten istiyor ve okuyor**:

```
summary.capacity     →  VsphereDatastore.CapacityBytes
summary.freeSpace    →  VsphereDatastore.FreeSpaceBytes
```

Ardından bu iki değer **hiçbir yere gitmiyor.** `Entity` üzerinde alan yok,
kimlik işareti yok, seri yok, alarm yok, ekranda gösterilmiyor. vCenter'dan her
turda ağ üzerinden çekiliyor ve `AddDatastores` içinde sessizce düşüyor.

> Ürün "datastore %95 dolu" diyemiyor — veri eksik olduğu için değil,
> **kullanılmadığı için.** Bu, eksik özellikten farklı bir şey: yapılan işin
> boşa gitmesi.

Aynısı VM için de geçerli olabilir; kontrol edilmedi. Bu belgenin bir sonraki
sürümünde "okunan ama kullanılmayan özellikler" ayrı bir bölüm olmalı.

---

## 5. Instance kimlikleri — neyle eşleştirilecek

Host'tan gelen datastore sayaçlarını ürünün Datastore varlığına bağlamak için
**VMFS UUID** gerekir; moref değil.

| Instance biçimi | Örnek | Ne | Envanterde karşılığı |
|---|---|---|---|
| VMFS UUID | `608bd301-3f719074-6962-f40343e85d10` | Datastore | **Yok.** `summary.url` (`ds:///vmfs/volumes/<uuid>/`) istenmiyor; eklenmesi gerekir |
| NAA | `naa.624a93701b3645f0b94b4abf000117fe` | LUN / cihaz | Yok |
| vmhbaN | `vmhba2`, `vmhba65` | HBA | Yok |
| FC yolu | `fc.<wwnn>:<wwpn>-fc.<wwnn>:<wwpn>-naa.*` | Fabric yolu | Yok |
| vmnicN | `vmnic4` | Fiziksel NIC | Yok |
| Sayı | `4000` | Sanal NIC (VM) | Yok |
| Boş | — | Tüm cihazların toplamı | — |

> Tablonun sağ sütunu, işin büyüklüğünü söyleyen yer. Host'tan datastore
> gecikmesi toplamak **sayaç eklemek değil**, envantere bir özellik ekleyip
> instance→varlık eşlemesi kurmaktır. Ürün bunu LUN'lar için zaten yapmıyor da:
> `disk.deviceLatency` serilerinin `naa.*` instance'ları hiçbir varlığa
> bağlanmıyor, host'un altında düz metin olarak duruyor.

> **Boş instance, eksik değer değildir.** Toplamdır. İkisini karıştırmak, bir
> cihazın verisini tüm cihazların ortalaması sanmak demektir.

---

## 5b. Sıfır, "hızlı" demek değildir — ölçüm tabanı

20 Eylül 2026'da canlı estate'te ölçüldü. Datastore gecikmesi host üzerinden
toplanmaya başladıktan sonra **41 datastore'un tamamı ölçülür hale geldi** ve
sonuç şu oldu:

| Sayaç | Sıfırdan büyük | Maks | Birim |
|---|---|---|---|
| `datastore.numberReadAveraged.average` | 33 / 302 | 63 | number |
| `datastore.numberWriteAveraged.average` | 76 / 302 | 205 | number |
| `datastore.totalReadLatency.average` | **0 / 302** | 0 | millisecond |
| `datastore.totalWriteLatency.average` | **0 / 302** | 0 | millisecond |
| `datastore.datastoreVMObservedLatency.latest` | **0 / 302** | 0 | millisecond |
| `datastore.siocActiveTimePercentage.average` | **0 / 302** | 0 | percent |

Aynı anda veritabanındaki örneklerde tek bir volume 3762 okuma + 8230 yazma
IOPS yapıyordu. **3762 IOPS yapan bir volume'ün gecikmesi 0 ms değildir.**

İki ayrı neden, ikisi de ölçüldü:

1. **`total{Read,Write}Latency` tam milisaniye olarak raporlanıyor.** Gözlenen
   tüm değerler tamsayı: 0, 1, 2. All-flash bir dizide (bu estate'te Pure)
   her istek ~0.3–0.8 ms'de dönerse sayaç 0 yazar. Ürün ekranda "0 ms" gösterir
   ve bu *ölçüm* gibi görünür — oysa "1 ms'nin altında, ne kadar altında
   bilinmiyor" demektir.
2. **`datastoreVMObservedLatency` yalnızca SIOC etkinken raporlar.** Mikrosaniye
   biriminde olması tam da bu çözünürlük sorunu için — ama SIOC bu estate'te
   302 host-volume çiftinin hiçbirinde etkin değil, dolayısıyla her yerde 0.

### Düzeltme (uzun pencere): "her zaman 0" değil, "%99,6 sıfır"

Yukarıdaki tablo **302 örneklik anlık bir pencereden**. 178 180 okumaya
çıkıldığında:

| Sayaç | Sıfırdan büyük | Oran | Maks |
|---|---|---|---|
| `datastore.totalReadLatency.average` | 669 / 178 180 | %0,38 | **7 ms** |
| `datastore.totalWriteLatency.average` | 254 / 178 180 | %0,14 | **8 ms** |
| `datastore.datastoreVMObservedLatency.latest` | 0 | %0 | SIOC kapalı |

**Sonuç doğruydu, ifade fazla kesindi.** Sayaçlar "hep 0" okumuyor: okumaların
%99,6'sı 0 çünkü 1 ms altı kesiliyor, ama **1 ms'yi aşan sivrilmeler
görünüyor**. Bu zaten sayaçları tutma gerekçesiydi — aşağıdaki "1 ms üstü,
yani asıl aranan sorun, doğru görünür" cümlesi.

Kısa bir pencereden mutlak bir cümle kurmak, bu belgenin uyardığı hatanın
kendi biçimi: *"0 / 302"* doğru bir gözlemdi, *"her zaman 0"* ondan çıkarılmış
yanlış bir genellemeydi.

### Sonuç: bu ürünün en tehlikeli sayı tipi

Bu, ürünün kaçınmak için kurulduğu "sessizlik sağlık gibi görünür" hatasının
daha kötü hâli: **sıfır, sessizlikten beterdir, çünkü sıfır ölçüm gibi
görünür.** Bir operatör "storage gecikmesi 0 ms" grafiğine bakıp depolamayı
eler; oysa ürün ona hiçbir şey söylememiştir.

Alınan kararlar:

- Sayaçlar **toplanmaya devam ediyor**. Yanlış değil, kaba: 1 ms'yi aşan bir
  gecikme — yani asıl aranan sorun — doğru şekilde görünür.
- `datastore.siocActiveTimePercentage.average` **kasıtlı olarak toplanıyor**.
  Performans sayısı değil; diğer ikisinin anlamlı olup olmadığının kanıtı. Bu
  kanıt kimsenin hafızasında değil, veritabanında durmalı.
- **Açık borç:** en iyi uygulama motoru yazıldığında ilk kurallardan biri şu
  olmalı — *"SIOC kapalı ve gecikme sayaçları sürekli 0 iken, bu estate'in
  depolama gecikmesi 1 ms altında ölçülemiyor; SIOC'u etkinleştirin."* Ürünün
  kendi körlüğünü söylemesi, kullanıcının tarif ettiği best-practice
  kontrollerinin tam örneğidir.
- `--from-store ... --mask` ile çalıştırılan probe artık bu tabloyu
  **"Sub-millisecond visibility"** başlığı altında kendiliğinden basıyor, yani
  yeni bir estate'te ilk gün sorulabilir.

---

## 5c. Depolama yolu (`storagePath`) ve kimlik zinciri

20 Eylül 2026'da toplanmaya başlandı. Merdivenin en alt basamağı: bir *yol*,
bir LUN'a giden tek rota. Canlıda host başına 126 yol, estate genelinde 1258
seri × 4 sayaç.

### İki farklı instance sözlüğü — aynı sayaç grubunda

Bu, ölçülmeden tahmin edilemeyecek bir şeydi ve önemli:

| Sayaç | Seviye | Instance biçimi | Toplanıyor mu |
|---|---|---|---|
| `storagePath.busResets.summation` | **2** | `vmhba0:C0:T0:L1` | **Evet** |
| `storagePath.commandsAborted.summation` | **2** | `vmhba0:C0:T0:L1` | **Evet** |
| `storagePath.totalReadLatency.average` | 3 | `fc.<init WWNN>:<init WWPN>-fc.<hedef WWNN>:<hedef WWPN>-naa.<LUN>` | Hayır — bırakıldı |
| `storagePath.totalWriteLatency.average` | 3 | aynı fabric biçimi | Hayır — bırakıldı |

Aynı yolu iki ayrı sözlükle adlandırıyorlar ve **doğrudan birbirlerine
eklenemezler.**

### Gecikme çifti neden bırakıldı

Kısaca toplandı, ölçüldü, sonra **bilinçli olarak çıkarıldı** (20 Eylül 2026
kararı). Maliyeti: bu estate'in 8 620 serisinin **2 536'sı** ve kararlı durum
projeksiyonunun yaklaşık **8 GB**'ı. Karşılığında cevapladığı soru "hangi yol
yavaş" — oysa §5b bu estate'te yol gecikmesinin **1 ms altında zaten
çözülemediğini** ölçtü. Pahalı bir sayaçtan, zaten alınamayan bir cevap.

Hata sayaçları ise "hangi yol **bozuk**" diyor, ki merdivenin en alt basamağı
bunun için var — ve **toplanan (summation) bir sayacın sıfırı bilgidir**,
kesilen bir ortalamanınki değil.

### Bunun bedeli, açıkça

Kalan iki sayaç yolu **çalışma zamanı adıyla** (`vmhba0:C0:T0:L1`) adlandırıyor
ve bu adda **LUN kimliği yok**. Bırakılan gecikme sayaçları ise initiator
WWPN, hedef (dizi) WWPN ve LUN NAA taşıyordu.

Sonuç: bir bus reset artık **host'a ve HBA'ya** atfedilebiliyor, ama
otomatik olarak bir **datastore'a** atfedilemiyor.

Bu, bir kablo ya da SFP için doğru granülarite — gidip bakacağın şey HBA'dır.
Datastore'a kadar bağlamak gerekirse yol, seri ödemek değil, host'un
`config.storageDevice.multipathInfo` haritasını okumaktır: çalışma zamanı adını
LUN NAA'sına çeviren tablo orada ve envanter ritminde bir kez okunur.

`IdentityMarkKind.WorldWideName` alanı, SAN switch ve storage toplayıcıları
geldiğinde hâlâ bunun için duruyor.

### Hangi zincir kuruldu, hangisi eksik

Veritabanına karşı ölçüldü:

```
VM ──BackedBy──> Datastore              ✓  (ilişki grafiği)
Datastore ──> VMFS UUID                 ✓  (VolumeIdentifier işareti, 41/41)
Datastore ──> host'tan gecikme          ✓  (§5b, 302 host-volume çifti)
LUN NAA ──> disk.deviceLatency          ✓  (43 cihaz instance'ı)
LUN NAA ──> storagePath (WWPN çiftleri) ✓  (42 LUN'ün 41'i disk cihazlarıyla eşleşiyor)

Datastore VMFS UUID ──> LUN NAA         ✓  KAPATILDI (aynı gün)
```

### Kopuk halka nasıl kapatıldı

Datastore'un `summary.url`'inden gelen kimlik bir **VMFS UUID**'dir; yolun ve
disk cihazının kimliği ise **NAA**'dır. İki sözlük hiç kesişmiyordu.

Host envanterine `config.fileSystemVolume.mountInfo` eklendi. Tel üzerindeki
şekli tahmin edilmedi, döküldü — ve tahmin yanlış çıkardı:

```xml
<HostFileSystemMountInfo>
  <mountInfo>                        <!-- iç eleman: path, accessMode -->
    <path>/vmfs/volumes/608bd301-...</path>
  </mountInfo>
  <volume xsi:type="HostVmfsVolume"> <!-- KARDEŞ eleman: asıl içerik -->
    <type>VMFS</type>
    <uuid>608bd301-3f719074-6962-f40343e85d10</uuid>
    <extent>
      <diskName>naa.600508b1001cb7368fc569b9146949ad</diskName>
    </extent>
  </volume>
</HostFileSystemMountInfo>
```

`volume`, `mountInfo`'nun **içinde değil yanında**. Özellik adının ima ettiği
yapı bu değil ve bunu bulmak bir yanlış denemeye mal oldu.

Bu okuma, ayrıştırıcıda **iç içe yapı** desteği gerektirdi. `triggeredAlarmState`
için yazılan tek seviyeli okuyucu, `volume` alt ağacının birleştirilmiş metnini
geri veriyordu — yerini aldığı düzleştiricinin yaptığı sessiz yalanın aynısı.
Artık `PropertyNode` ağacı var: ad, metin, tip, çocuklar.

Sonuç canlıda: **41/41 datastore hem `VolumeIdentifier` hem `StorageDeviceId`
işareti taşıyor.** Zincir uçtan uca sorgulanabilir:

| Datastore | Üzerindeki VM | Datastore serisi | LUN cihaz serisi | Yol serisi | Ayrı initiator WWPN |
|---|---|---|---|---|---|
| PRODVOL10 | 14 | 60 | 1 | 80 | 20 |
| PRODVOL3 | 13 | 60 | 1 | 80 | 20 |
| PRODVOL8 | 12 | 60 | 1 | 80 | 20 |

Yani "SQL Server yavaş → hangi datastore → hangi LUN → 10 host × 2 HBA = 20
yoldan hangisi" sorusu artık tek bir SQL sorgusuyla cevaplanabiliyor. Ürünün
imzası olan teşhis merdiveninin **veri temeli tamamlandı**; kalan iş bu veriyi
okuyan analiz katmanı.

**Not:** Yayılmış (spanned) VMFS volume'lerde birden fazla `extent` olabilir ve
hepsi işaretleniyor. Sadece ilkini almak, bir datastore'u tek LUN üzerinde
gösterip diğerlerini sessizce kaybetmek olurdu — ve yayılmış bir volume'de
kaybolan yarı, çoğu zaman kesintiyi açıklayan yarıdır.

### Sıfırın iki türü

§5b'deki gecikme sıfırlarının aksine, buradaki sıfırlar **bilgidir**:

```
storagePath.busResets.summation        0 / 7608 örnek sıfırdan büyük
storagePath.commandsAborted.summation  0 / 7608 örnek sıfırdan büyük
```

Bu iki sayacın toplanmaya devam etmesinin, gecikme çiftinin bırakılmasının
sebebi tam olarak budur: burada sıfır bir cevaptır.

Fark, sayacın türünde. **Kesilen (truncate edilen) bir ortalama sıfırı hiçbir
şey söylemez; toplanan (summation) bir sayacın sıfırı "hiç olmadı" der.** Bu
estate'in fabric'inde 7608 örnek boyunca tek bir bus reset ya da iptal edilmiş
komut yok — bu gerçek ve iyi bir sağlık sinyali. Ürün bu ikisini birbirine
karıştırmamalı.

---

## 5d. `mem.state.latest` — satıcının yayımladığı tek eşik

20 Eylül 2026'da eklendi. HostSystem üzerinde, instance yok, host başına tek
seri.

Bu dosyadaki diğer her sayaçtan farklı bir şey: **anlamı platformla birlikte
geliyor.** Değer bir sayı değil, yayımlanmış bir enum.

| Değer | Ad | Ne demek |
|---|---|---|
| 0 | high | Boş bellek bol, geri kazanım yok |
| 1 | soft | Balon şişiyor — mekanizma çalışıyor |
| 2 | hard | Sıkıştırma ve takas devrede |
| 3 | low | Host agresif geri kazanımda; üstündeki her VM aynı anda bozuluyor |

> **Cevapladığı soru:** "Bu host belleği geri kazanıyor mu, ve ne kadar
> sertlikte?" — vROps araştırmasının kurduğu şey tam da buydu: Broadcom
> neredeyse hiçbir yerde eşik yayımlamıyor. Bu ürünün yazacağı her bellek
> kuralı birinin uydurduğu bir çizgidir; **bu değil.** "hard" ya da "low",
> tartışılacak bir seviye değil, satıcının kendi verdiği hükümdür.

### Neden `IsFaultCount` değil

Denendi ve **iki ayrı yerden** tutmadı. İkisini de yazmak gerekiyor, çünkü
birincisi düzeltilse ikincisi kalırdı:

1. **Burada sıfır *iyi* demek.** `IsFaultCount` bayrağının verdiği söz,
   sıfırın "hiç olmadı" anlamına gelmesidir (§5c). Burada 0 = `high` =
   sağlıklı. Bayrağın sözü tam tersine dönüyor.
2. **`FaultCounters` toplam (aggregate) instance'ları atlıyor.** Bu sayacın
   instance'ı her zaman boştur. Yani bayrak konsa bile kural onu hiç okumaz —
   yanlış *ve* ölü bir bayrak olurdu.

### Peki bir kurala nasıl ulaşmalı

`CounterValue` bunu **taşıyamıyor.** Taşıdığı şey `Raw = 2.0` ve
`Unit = "number"`; bir kural bunu okuyup "2 kötüdür" diyebilmek için
`mem.state.latest` adını bilmek zorunda kalır — ki uygulama katmanının bir
vim25 sayacını adlandırması yasak (bkz. `FaultCounters` ve ADR-0005).

Gereken şey bir **bayrak değil, bir kırılma noktası**: toplayıcının
bildirdiği "şu değerden itibaren bozulmuş" tamsayısı. `IsFaultCount` bir
bool olabildi çünkü sorusu evet/hayırdı; burada soru "hangi değerden sonra"
ve cevabı satıcıdan geliyor, yani veri taşınmalı, tip değil.

**Bugün eklenmedi ve bu bilinçli.** Bayrağı tüketecek kural yok; şimdi
eklemek `CounterValue`'ya okunmayan bir alan ve ayrıştırıcıya boşa bir dal
koymak olur — `IsFaultCount` ve `InstanceIsVantagePoint`'in ikisi de
kendilerini okuyan kuralla **aynı anda** geldiler. Sayaç bugün toplanıyor ki
kural yazıldığında karşısında üç aylık geçmiş bulsun; kural yazıldığında
`CounterValue.DegradedAtOrAbove` (int?) eklenecek ve bu satır ona işaret
ediyor.

---

## 5e. Düşen paketler: `summation` ama **arıza değil**

20 Eylül 2026'da eklendi. `net.droppedRx.summation` ve
`net.droppedTx.summation`, hem HostSystem hem VirtualMachine üzerinde.

> **Cevapladığı soru:** "Yavaşlığın sebebi ağ mı?" — ve bugüne kadar ürünün
> bu soruya verebileceği hiçbir cevabı yoktu. **Bu ürün hiçbir türde ağ
> verisi toplamıyordu.** Düşen paket fırtınası sahada "uygulama yavaş" diye
> görünür; depolama ve CPU sayılarının hepsi temizdir. Yani operatör,
> sorunun hiç bulunmadığı merdivenden aşağı iner — bu ürünün var olma sebebi
> olan körlüğün ta kendisi.

### Neden `storagePath` faultları ile aynı kutuya konmadılar

İlk bakışta aynı argüman geçerli: ikisi de `summation`, dolayısıyla
**sıfırları gerçektir** (§5c'deki ayrım). Toplanma gerekçesi gerçekten de
odur.

Ama `IsFaultCount` bundan fazlasını söylüyor: *hiçbir* sıfırdan büyük okuma
kabul edilebilir değildir. Bu, bus reset için doğru — SCSI, dizi meşgul diye
bus'ı sıfırlamaz. Düşen paket için **doğru değil**: yoğun bir uplink'te
küçük ve sürekli bir düşme oranı normaldir.

Ve `FaultCounters`'ın **eşiği yok, hafızası yok**. Bayrak konsaydı, sağlıklı
bir estate'te tek bir düşen çerçeve her turda bir uyarı açardı — yani ürün,
bu bayrağın görünür kılmak için var olduğu bus reset'i kendi gürültüsüne
gömerdi. Bunlar bir **oran** ve içinden geçirilecek bir çizgi istiyor; o,
başka bir kural.

### Neden cihaz başına tutulmuyorlar

`KeepPerDevice` bilerek `net.` ile eşleşmiyor. Host başına ~16 vmnic × 2
sayaç × 10 host ≈ **320 seri**, "hangi uplink" sorusu için. Toplam serinin
cevapladığı soru "bu host hiç paket düşürüyor mu" — ki bugün hiçbir şey onu
cevaplamıyor. İkinci soru, bir şeyin düştüğü *görüldükten sonra*, tek satır
değiştirilerek satın alınabilir. CPU için verilen kararın aynısı (§KeepPerDevice).

---

## 5f. 20 Eylül eklemelerinin maliyeti

ADR-0017'nin ölçülmüş rakamlarıyla: 6 084 seri ≈ 13,6 GB, yani **seri başına
≈ 2,24 MB** kararlı durumda.

| Ekleme | Nesne | Cihaz başına mı | Seri | ≈ Disk |
|---|---|---|---|---|
| `disk.busResets.summation` | Host | **Evet** (32 + toplam) × 10 | 330 | 0,74 GB |
| `disk.commandsAborted.summation` | Host | **Evet** | 330 | 0,74 GB |
| `disk.scsiReservationConflicts.summation` | Host | **Evet** | 330 | 0,74 GB |
| `mem.state.latest` | Host | Hayır | 10 | 0,02 GB |
| `mem.swapinRate` / `swapoutRate` / `compressionRate` / `decompressionRate` | Host | Hayır | 40 | 0,09 GB |
| `mem.active.average` | VM | Hayır | 145 | 0,33 GB |
| `net.droppedRx` / `droppedTx` | Host + VM | Hayır | 310 | 0,69 GB |
| **Toplam** | | | **≈ 1 495** | **≈ 3,4 GB** |

6 084 → **≈ 7 579 seri** (+%24,6), 13,6 GB → **≈ 17,0 GB**.

**Bunun üçte ikisi üç sayaçtan geliyor**, ve bu sayaç listesine bakarak
görülmez: `KeepPerDevice` zaten `disk.*` ile eşleştiği için her biri LUN
başına bir seri. Üç sayaç, 990 seri.

Buna rağmen tutuluyorlar, ve gerekçe §5c'nin kendisi: yol faultları yolu
**çalışma zamanı adıyla** adlandırıyor ve o adda LUN kimliği yok, dolayısıyla
bir reset host'a ve HBA'ya atfedilebiliyor ama datastore'a
atfedilemiyor. `disk.*` faultları `naa.*` taşıyor ve §5c'de kapatılan zincir
onu datastore'a ve üstündeki VM'lere çeviriyor — aranan cümle o. Ayrıca
estate zaten yol faultları için ~2 500 seri ödüyor; bu, onun %40'ı.

> **Kesmek gerekirse ne kesilecek, şimdiden yazılı:** `busResets` ve
> `commandsAborted`, `storagePath` karşılıklarıyla örtüşüyor — gidecek olan
> onlar. `scsiReservationConflicts`'ın hiçbir yerde karşılığı yok.
> 20 Eylül'de `storagePath` gecikmesinin çıkarılması (2 536 seri, ~8 GB,
> cevaplanamayan bir soru için) emsaldir; buradaki fark, sorunun
> cevaplanabilir olması.

### Bırakılması önerilenler

- **`net.*` cihaz başına** — 320 seri, "hangi uplink". Toplam seri, asıl
  körlüğü zaten kapatıyor. (§5e)
- **`storagePath.scsiReservationConflicts`** diye bir şey aranmadı: yol
  düzeyinde yok, cihaz düzeyinde var.
- **`mem.usage` ve `mem.vmmemctl`'i bellek baskısı grubuna almak** —
  Dynatrace'in `esxiHighMemoryDetection`'ı bu ikisini açıkça dışarıda
  bırakıyor, ve gerekçesi bu belgenin diliyle aynı: balon, mekanizmanın
  *çalışmasıdır*; konsolide bir host'ta yüksek `mem.usage` normaldir.

---

## 5h. `cpu.maxlimited.summation` — yanlış cevabı susturmak değil, değiştirmek

20 Eylül 2026'da eklendi. VirtualMachine üzerinde, toplam (aggregate) instance,
milisaniye cinsinden `summation`.

> **Cevapladığı soru:** "Bu VM neden çekirdek bekliyor — host doluysa mı, yoksa
> kendi tavanı mı?" `cpu.ready.summation` bu ikisini **ayıramaz.** Kendi CPU
> limiti altında tutulan bir VM, host'u dolu olan bir VM ile birebir aynı
> bekleme süresini biriktirir. CPU kuralı bu yüzden limitlenmiş makineyi
> "host'un kurbanı" ilan ediyordu.

Bu, ürünün en çok kaçındığı hata tipiydi ve yazarı tarafından **sessizce
gönderilmek yerine kendi raporunda bildirilmişti**. Maliyeti iki katlı:

1. Operatöre "host CPU'suz" denir, gidip host'a bakar, host **iyidir**, ve bir
   daha bu uyarıya inanmaz.
2. VM'in **asıl** sorunu — muhtemelen yıllar önce, muhtemelen yanlışlıkla
   konmuş bir CPU limiti — hiçbir ekranda görünmez.

Dynatrace aynı ayrımı `guestCpuLimitReached` ile yapıyor: aynı iki sinyal, artı
ayıran üçüncü terim. Yapı yayımlanmış, **seviye yayımlanmamış** (§ CpuContention
politikası).

### Neden bu sayaç ve neden ucuz

| Özellik | Değer | Sonucu |
|---|---|---|
| Nesne | VirtualMachine | Limit makinenin özelliği; host'ta karşılığı yok |
| Rollup | `summation` | `cpu.ready` ile aynı — yeni dönüşüm makinesi gerekmiyor |
| Birim | millisecond | `AsPercentageOfInterval` aynen uygulanır |
| Instance | toplam (per-vCPU bırakıldı) | `KeepPerDevice` `cpu.` ile eşleşmiyor (§6.5) |
| `IsFaultCount` | **Hayır** | Aşağıda |
| Seri maliyeti | 145 VM × 1 = **145 seri ≈ 0,33 GB** | `mem.active.average` ile aynı büyüklük |

**Sıfırı yapısaldır.** Limit tanımlanmamış bir VM'de vSphere buraya hiç
biriktirmez. Yani sıfırdan büyük bir okuma yorumlanacak bir *seviye* değil,
**şu anda ısıran bir tavanın varlığıdır.** §5c'nin "toplanan bir sayacın sıfırı
bilgidir" ayrımının en temiz örneği.

### Neden `IsFaultCount` değil

§5e'deki düşen paket argümanının daha keskin hâli. CPU limiti **birinin
seçtiği bir yapılandırmadır**. Lisans gereği ya da test amacıyla kasıtlı olarak
sınırlanmış bir VM'de her turda ısırır ve bu tamamen doğrudur. Sıfırdan büyük
okuma bir *arıza* değil, bekleme süresinin *açıklamasıdır*. Bayrak konsaydı,
doğru yapılandırılmış her makine kalıcı bir uyarı üretirdi.

### Bu sayacın diğerlerinden farkı: **bir hükmü susturuyor**

Bu dosyadaki başka hiçbir sayaç bunu yapmıyor. Diğerleri bir soruya cevap
*ekler*; bu, mevcut bir cevabı **bastırır** (bkz. `CpuContentionPolicy.MaxLimitedPercent`).
Sonucu §5g için doğrudan bir risk artışıdır ve orada ayrıca yazıldı:

> Adı yanlışsa sayaç hiç gelmez, bastırma hiç çalışmaz, ve kural **kapatılan
> yanlış pozitife sessizce geri döner** — ürün sağlıklı görünürken. Eksik bir
> gecikme sayacı bir cevabı kaybettirir; bu, kaybedilmiş bir cevabı *yanlış* bir
> cevapla değiştirir.

Bu yüzden kural, bastırdığı her makineye **kendi hükmünü veriyor**: bastırılan
ile adı konan küme birebir aynı. Sessizlik, yanlış cevabın kabul edilebilir
ikamesi değil.

---

## 5g. Bu bölüm **ölçülmedi** — §6.4'ün ihlali, bilerek

Bu belgenin ilk satırı, içindeki hiçbir şeyin hatırlanarak yazılmadığını
söylüyor. §5d, §5e ve §5f **bu kuralın istisnasıdır ve öyle işaretlenmiştir.**

Eklenen on sayacın adı ve seviyesi **canlı bir vCenter'a sorulmadı.**
Worktree'den erişilebilir bir vCenter yok ve kimlik bilgisi aranmadı.
Depodaki tek katalog, testlerin fixture'ı, yalnızca `datastore`,
`storagePath` ve `virtualDisk` gruplarını kapsıyor — yani `disk.*`, `mem.*`
ve `net.*`'in hiçbiri kontrol edilebilir durumda değil.

| Sayaç | Nesne | Beklenen seviye | Doğrulandı mı |
|---|---|---|---|
| `disk.busResets.summation` | Host | 2 | **Hayır** |
| `disk.commandsAborted.summation` | Host | 2 | **Hayır** |
| `disk.scsiReservationConflicts.summation` | Host | 2 | **Hayır** |
| `mem.state.latest` | Host | 2 | **Hayır** |
| `mem.swapinRate.average` | Host | 2 | **Hayır** |
| `mem.swapoutRate.average` | Host | 2 | **Hayır** |
| `mem.compressionRate.average` | Host | 2 | **Hayır** |
| `mem.decompressionRate.average` | Host | 2 | **Hayır** |
| `mem.active.average` | VM | 2 | **Hayır** |
| `net.droppedRx.summation` | Host + VM | 2 | **Hayır** |
| `net.droppedTx.summation` | Host + VM | 2 | **Hayır** |
| `cpu.maxlimited.summation` | VM | 2 | **Hayır — ve bu en riskli satır** |

"Beklenen seviye" sütunu satıcı belgelerinden gelir, **ölçümden değil.**
Hiçbirinin **seviye 3** olmadığına inanılıyor — eğer biri öyleyse bu, bir
kurulum ön koşulunu değiştirir ve yüksek sesle söylenmesi gerekir. Mevcut
`InsufficientDetailLevel` mekanizması eksik sayacı adıyla raporluyor ve
yeni üç `disk.*` faultunun da o yoldan geçtiği testle sabitlendi — yani
seviye yanlışsa **sessiz kalmaz.** Ama bir fault sayacını sessizce
kaybetmek, bir gecikme sayacını kaybetmekten kötüdür: sıfırlarına
güvenilmesi gerekiyordu.

Adın yanlış olması ise hiç raporlanmıyor bile — `ProtocolError` olarak geçer
ve §0'ın anlattığı hikâye tam olarak budur.

> **`cpu.maxlimited.summation` bu tablodaki diğerlerinden farklı bir risk
> taşıyor ve ayrı yazılması gerekiyor.** Bu listedeki her sayaç, adı yanlışsa
> *bir cevabı kaybettirir*. Bu sayaç, adı yanlışsa `CpuContention`'ın bastırma
> kapısını hiç açtırmaz ve kural **kapattığı yanlış pozitife sessizce geri
> döner** — yani kaybedilen bir cevabın yerini *yanlış* bir cevap alır. Sürüme
> girmeden önce `--map` ile doğrulanması gereken ilk satır budur.
>
> Kısmi bir teselli var ve ölçüsü kayda değer: bastırma yalnızca sayaç
> geldiğinde çalışır, dolayısıyla sayacın gelmemesi bugünkü davranışı aynen
> korur. Yani hata **sessiz bir gerileme**dir, yeni bir kusur değil — ama
> tam olarak §0'ın "boş cevap, hata değildir" biçimi.

> **Yapılacak iş, tek cümle:** bu sayaçlar bir sürüme girmeden önce
> `--map` bir kez çalıştırılıp yukarıdaki tablonun sağ sütunu doldurulmalı,
> ve `disk`, `mem`, `net` grupları test fixture'ına **gerçek vCenter
> çıktısından yapıştırılmalıdır** — elle yazılarak değil, ki fixture'ın
> başlığı bunu ayrıca söylüyor.

Fixture'ın kapsamadığı sayaçların listesi artık bir teste yazılı
(`Every_counter_no_catalogue_in_this_repository_can_check_is_listed_rather_than_assumed`),
böylece kontrol edilmeyen bir gruba sayaç eklemek de, bir grubu fixture'a
ekleyip geri dönüp bakmamak da sessizce geçmiyor.

---

## 6. Bu haritanın dayattığı sonuçlar

1. **Datastore gecikmesi host'tan toplanmalı**, instance→VMFS UUID eşlemesiyle
   Datastore varlığına atfedilerek. Datastore nesnesine sormak çalışmaz.
   *(20 Eylül 2026'da yapıldı. `summary.url`'den çıkarılan volume kimliği
   `VolumeIdentifier` kimlik işareti olarak saklanıyor; 41/41 datastore'da
   mevcut ve bir host'un bildirdiği 30 instance'ın 30'u çözümlendi. Seri
   instance'ı artık ölçümü yapan host'un adı — böylece "her host'tan yavaş"
   (dizi/fabric) ile "tek host'tan yavaş" (o host'un HBA/kablo/path'i)
   ayrılabiliyor. Ama önce §5b okunmalı: sayılar toplanıyor, çözünürlükleri
   sınırlı.)*
2. **Datastore doluluğu için yeni veri gerekmiyor.** `summary.capacity` ve
   `summary.freeSpace` zaten her turda okunuyor ve atılıyor; onları varlığa
   taşımak en yüksek getirili ve en ucuz iş. Geçmiş eğilim isteniyorsa
   `disk.capacity/used/provisioned.latest` seviye 1'de hazır bekliyor.
3. **`storagePath` kısmen seviye 2'dir.** İlk tespit eksikti: `busResets` ve
   `commandsAborted` **seviye 2**, yani ürünün zaten şart koştuğu seviyede
   geliyor — ve bunlar eşik değil, arıza sinyalidir. Gecikme çifti seviye 3;
   yine de isteniyor, eksikse mevcut mekanizma `InsufficientDetailLevel`
   olarak sayaç adıyla birlikte raporluyor. Bkz. §5c.
4. **Bir sayacın nesnesi, adından çıkarılamaz.** Yeni bir sayaç eklenirken
   `--map` çıktısına bakılmadan eklenmemeli.
5. **Bir sayacı cihaz başına tutmak, sayaç saymakla görünmez.**
   `KeepPerDevice` bir *önek* ile eşleşiyor, tek tek sayaçlarla değil. Yani
   `disk.` ile başlayan yeni bir sayaç, kimse karar vermeden LUN başına bir
   seri olur: 20 Eylül'de eklenen üç fault sayacı, üç satır kod ve **990
   seri**. Yeni bir sayacın maliyeti, hangi öneke düştüğüne bakılmadan
   tahmin edilemez. (§5f)
6. **`summation` olmak, arıza olmak değildir.** İkisi ayrı sorular:
   "sıfırı bilgi midir" ve "sıfırdan büyük her okuma arıza mıdır". Yol
   faultlarında ikisinin de cevabı evet; düşen pakette birincisi evet,
   ikincisi hayır. `IsFaultCount` ikincisini söyler. (§5e)
7. **Satıcının yayımladığı bir enum, uydurulmuş bir eşikten değerlidir** —
   ve ürünün bunu taşıyacak yolu henüz yok. `CounterValue` bir bool
   taşıyabiliyor, bir kırılma noktası taşıyamıyor. (§5d)
8. **Boş performans cevabı hata değildir.** Ürün artık bunu raporluyor
   (`PartialFailures`), ama tasarım varsayımı olarak da akılda tutulmalı: vSphere
   entegrasyonunda *sessizlik* en sık görülen hata biçimidir.

---

## 7. Haritayı yeniden üretmek

```bash
dotnet run --project tools/EnterpriseObservatory.VsphereProbe -c Release -- \
  --from-store <observatory.db yolu> --map
```

`--from-store`, bağlantıyı ürünün kendi veritabanından okur ve kendi anahtar
zinciriyle çözer. Parola yeniden yazılmaz — çünkü yeniden yazmak masum bir işlem
değildir: PowerShell çift tırnak içinde `$` ve ters tırnağı yorumlar ve bu ürün,
baştan beri doğru olan bir parolanın reddedilmesini bir gün boyunca kovaladı.

Adları gizlemek için `--mask` ekleyin.

Başka bir estate'te çalıştırıldığında sayılar değişir; **yapı değişmez.** Değişirse
bu belge güncellenir — hatırlanarak değil, yeniden ölçülerek.
