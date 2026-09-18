# ADR-0003: Topoloji-önce varlık modeli

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0001, ADR-0004

## Bağlam

Önceki üründe veri modelinin merkezi `MonitoringSnapshot` idi: her poll
döngüsünde üretilen, içinde `Hosts`, `VirtualMachines`, `Clusters`,
`Datastores`, `IloNodes`, `IdracNodes`, `OneViewServers`, `OmeDevices`,
`SimplivityNodes`, `StorageArrays` gibi **paralel listeler** taşıyan bir nesne.

Varlıklar arasındaki ilişkiler bu modelde birinci sınıf değildi; kod içinde
arama ve eşleştirme mantığı olarak yaşıyordu:

- `HostInventoryNormalizer.SharesIdentity` — iLO kaydını ESXi host'a katlamak
  için UUID / IP / DNS karşılaştırması
- `OneViewAlerting.MatchesIloInventory` — seri numarası / mpHostName / mpIpAddress
- `StorageArrayDatastoreMap` — array ↔ datastore eşlemesi
- `VmDatastorePlacement` — VM ↔ datastore

Bu yaklaşım çalışıyordu ama üç yapısal sonucu vardı:

1. **İlişki mantığı dağıldı.** Aynı "bu iki kayıt aynı fiziksel kutu mu?"
   sorusu birden fazla yerde, birbirinden habersiz uygulandı.
2. **İlişkiler sorgulanamıyordu.** "Bu datastore'a bağlı VM'ler hangi host'larda
   ve o host'ların HBA'ları hangi switch portuna gidiyor?" sorusu, ürünün
   *asıl değer önermesi* olmasına rağmen, modelin kendisinden cevaplanamıyordu;
   her seferinde özel kod gerekiyordu.
3. **Kök neden analizi zayıf kaldı.** Etki yayılımı (bir SFP arızasının hangi
   VM'leri etkilediği) ilişki grafiği olmadan güvenilir hesaplanamaz.

Referans platformlar bu problemi çözmüş durumda. Dynatrace'in **Smartscape**
modeli, izlenen her şeyi *varlık* ve *ilişki* olarak modelleyip metrikleri
bunların üzerine asar; kök neden analizi (Davis) bu grafiği gezerek çalışır.
VMware Aria Operations'ın adapter modeli de aynı şekilde "hedefe bağlan,
metrik/özellik/olay taşıyan **nesneler** oluştur ve nesneler arasına **ilişki**
ekle" olarak tanımlıdır.

Bizim ürünümüzün farklılaştırıcısı zaten fiziksel katman korelasyonu — yani
ilişkiler. İlişkileri ikinci sınıf vatandaş yapmak, ürünün en güçlü iddiasını
en zayıf temele oturtmak olur.

## Karar

Çekirdek veri modeli **varlık grafiği** olsun; snapshot bu grafiğin zaman
içindeki bir görüntüsü olsun, tersi değil.

- **Varlık (`Entity`)** — kararlı bir kimliği (`EntityId`), bir türü
  (`EntityKind`: EsxiHost, VirtualMachine, Cluster, Datastore, PhysicalServer,
  Bmc, SanSwitch, SanPort, StorageArray, Volume…) ve bir kaynak kimlik kümesi
  (`SourceIdentity`: her collector'ın o varlığı nasıl gördüğü) olan nesne.
- **İlişki (`Relationship`)** — iki varlık arasında tipli, yönlü kenar
  (`Runs`, `ConnectedTo`, `BackedBy`, `MemberOf`, `ManagedBy`, `PoweredBy`).
- **Gözlem (`Observation`)** — bir varlığa asılan metrik örneği, özellik veya
  olay. Metrikler varlıktan bağımsız var olamaz.
- **Kimlik çözümleme (`IdentityResolver`)** tek bir yerdedir. Bir collector
  "şu kimlik işaretleriyle bir varlık gördüm" der; hangi mevcut varlığa
  denk düştüğüne resolver karar verir. Collector'lar bu kararı vermez.

Snapshot kavramı korunur ama türetilmiş hale gelir: belirli bir andaki grafiğin
değişmez bir projeksiyonu.

## Gerekçe

**Neden ilişkiler birinci sınıf?** Ürünün cevap vermeyi vaat ettiği soru
("sorun hangi kabloda ve hangi VM'leri etkiliyor") doğası gereği bir graf
sorgusudur. Graf modeli olmadan bu soru her seferinde elle yazılmış eşleştirme
koduyla cevaplanır — önceki üründe tam olarak bu oldu.

**Neden kimlik çözümleme tek yerde?** Önceki üründe aynı mantık en az dört
yerde tekrarlanıyordu. Her tekrar, biraz farklı davranan bir kopyadır; zamanla
bu kopyalar birbirinden ayrışır ve hangisinin doğru olduğu belirsizleşir.
Tek bir resolver, kural değişikliğinin tek noktadan yapılmasını sağlar ve
"aynı kutu iki kez sayıldı" sınıfı hataları yapısal olarak imkânsızlaştırır.

**Neden collector kimlik kararı vermez?** README'deki 2. ilkenin
(*katman başına tek gerçek kaynağı*) mimari karşılığı budur. Bir BMC
collector'ı "bu bir ESXi host'tur" diyebilirse, er ya da geç sahte host üretir.
Collector yalnızca gözlemini ve gördüğü kimlik işaretlerini bildirir.

## Değerlendirilen alternatifler

### Alternatif A: Önceki modeli koru (paralel listeler + eşleştirme kodu)

Bilinen, çalışan, test edilmiş. En düşük riskli.

Seçilmedi çünkü yukarıdaki üç yapısal sonuç, ürünün büyümek istediği yönün
(fiziksel triangülasyon, etki analizi, kök neden) tam olarak önünde duruyor.
Bu borcu şimdi ödemezsek, her yeni korelasyon özelliği onu büyütür.

### Alternatif B: Harici graf veritabanı (Neo4j vb.)

Graf sorgularını en güçlü şekilde destekler.

Seçilmedi çünkü ADR-0001'deki dağıtım kısıtıyla çelişiyor: ürün ek bir
appliance veya sunucu gerektirmeden tek MSI ile kurulmalı. Grafın bugünkü
büyüklüğü (birkaç bin varlık) bellek içi bir yapıyla rahatça karşılanır.
Kalıcılık için ilişkiler ilişkisel tabloda tutulabilir.

### Alternatif C: Tam OpenTelemetry veri modeli benimse

Standart, araç ekosistemi geniş.

Seçilmedi (şimdilik) çünkü OTel'in kaynak (resource) modeli altyapı
topolojisindeki *tipli ilişkileri* birinci sınıf ifade etmiyor; bizim asıl
ihtiyacımız o. Dışa aktarım katmanında OTel uyumluluğu ayrıca ele alınacak —
bu bir model kararı değil, bir adaptör kararı.

## Sonuçlar

### Olumlu

- Etki analizi ve kök neden, özel kod yerine graf gezintisi olur.
- Yeni bir satıcı eklemek yeni bir ilişki tipi eklemekten ibarettir.
- Kimlik çözümleme kuralları tek yerde test edilir.
- "Aynı kutu iki kez" hatası model seviyesinde engellenir.

### Olumsuz / kabul ettiğimiz bedel

- **Basit senaryolar için fazladan katman.** "Host listesini göster" artık
  doğrudan bir liste okumak değil, graftan projeksiyon almak.
- Graf tutarlılığı yeni bir sorumluluk: kaybolan varlıkların ilişkileriyle ne
  olacağı (tombstone, yaş sınırı) açıkça tasarlanmalı.
- Kimlik çözümleme yanlış eşleştirirse hata artık *tek bir yerde* ama
  *her yeri* etkiler. Bu yüzden resolver'ın test kapsamı en yüksek olmalı.
- Bellek içi graf, dağıtım topolojisi bölünürse paylaşılmalı — ADR-0001'deki
  `ISnapshotChannel` soyutlaması bunu kapsamalı.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Varlık sayısı bellek içi grafı zorlarsa (>100k varlık) kalıcı graf deposu
  değerlendirilmeli.
- Müşteri tarafında OTel tabanlı bir gözlemlenebilirlik yığınıyla entegrasyon
  birincil gereksinim haline gelirse, model hizalaması yeniden ele alınmalı.
