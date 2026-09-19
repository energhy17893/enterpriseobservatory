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

## 6. Bu haritanın dayattığı sonuçlar

1. **Datastore gecikmesi host'tan toplanmalı**, instance→VMFS UUID eşlemesiyle
   Datastore varlığına atfedilerek. Datastore nesnesine sormak çalışmaz.
2. **Datastore doluluğu için yeni veri gerekmiyor.** `summary.capacity` ve
   `summary.freeSpace` zaten her turda okunuyor ve atılıyor; onları varlığa
   taşımak en yüksek getirili ve en ucuz iş. Geçmiş eğilim isteniyorsa
   `disk.capacity/used/provisioned.latest` seviye 1'de hazır bekliyor.
3. **`storagePath` seviye 3 gerektirir.** SFP/fabric teşhisi istiyorsak bu, ürünün
   operatörden isteyeceği bir yapılandırma — ve isteyeceğini söylemesi gerekir.
4. **Bir sayacın nesnesi, adından çıkarılamaz.** Yeni bir sayaç eklenirken
   `--map` çıktısına bakılmadan eklenmemeli.
5. **Boş performans cevabı hata değildir.** Ürün artık bunu raporluyor
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
