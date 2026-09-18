# ADR-0004: İlişki taksonomisi ve varlık yaşam döngüsü

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-18
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0003 (bu kararı gerektiren model), ADR-0005 (alarm yaşam döngüsü)

## Bağlam

ADR-0003 varlık grafiğini çekirdek model yaptı ama ilişki tiplerini kasıtlı
olarak belirsiz bıraktı — örnek olarak altı ad saydı, taksonomiyi sabitlemedi.
Bu ADR o boşluğu dolduruyor.

İki soru açıktı:

1. **Hangi ilişki tipleri?** Ürünün asıl vaadi şu zinciri çözmek:
   `VM → Datastore → LUN → Array Port → SAN Switch Port → HBA → ESXi Host`.
   Tek bir genel `ConnectedTo` tipi bu zinciri modelleyebilir ama fiziksel bir
   kabloyla mantıksal bir yerleşimi ayırt edemez. "Bu SFP arızası hangi VM'leri
   etkiler" sorusu tam olarak bu ayrımı gerektiriyor.

2. **Kaybolan varlığa ne olur?** Bir host envanterden düştüğünde silinirse ona
   bağlı açık alarmlar ve geçmiş veriler ne olacak? Host bakıma alındığında
   veya geçici olarak vCenter'dan düştüğünde varlığı yok saymak yanlış olur.

### Referans platformların yaklaşımı

**Dynatrace** adaptör başına serbest kenar üretmiyor. Sabit, altı tipli bir
semantik sözlük kullanıyor: `CALLS`, `CHILD_OF`, `INSTANCE_OF`, `PART_OF`,
`RUNS_ON`, `SAME_AS`. Kapalı bir kelime dağarcığı olması, ilişkilerin
sorgulanabilir ve analiz edilebilir kalmasını sağlıyor — her entegrasyon kendi
kenar tipini uydurursa graf sorgulanamaz hale gelir.

`SAME_AS`'in varlığı özellikle öğretici: Dynatrace kimlik denkliğini
*birleştirme* (merge) olarak değil, **ilişki** olarak modelliyor. İki kaydın
aynı şey olduğu bilgisi grafın kendisinde yaşıyor, kayıtlar yok edilmiyor.

**VMware Aria Operations** yalnızca parent-child ilişkisi tutuyor ama iki
kritik kısıtla:
- İlişkiler **döngü içeremez**; döngü analitik hesaplamaları bozar.
- **Çocuğun sağlığı ebeveynin sağlığını etkiler** — sağlık yayılımı
  containment kenarlarını izler.

Kaynaklar:
[Dynatrace semantic dictionary](https://docs.dynatrace.com/docs/semantic-dictionary/model/dt-entities),
[Aria Operations object relationships](https://techdocs.broadcom.com/us/en/vmware-cis/aria/aria-operations/8-16/vmware-aria-operations-configuration-guide-8-16/configuring-objects/object-discovery/managing-objects-in-your-environment/configuring-object-relationships.html),
[Aria Operations Integration SDK](https://vmware.github.io/vmware-aria-operations-integration-sdk/guides/adding_to_an_adapter/)

## Karar

### 1. Kapalı, altı tipli ilişki sözlüğü

Collector'lar kendi ilişki tiplerini **uyduramaz**. Yeni bir tip eklemek bir
ADR gerektirir.

| Tip | Yön | Örnek | Endüstri karşılığı |
|---|---|---|---|
| `PartOf` | çocuk → ebeveyn | `HbaPort PartOf EsxiHost`, `EsxiHost PartOf Cluster`, `Dimm PartOf PhysicalServer` | Dynatrace `PART_OF`, Aria parent-child |
| `RunsOn` | konuk → barındıran | `VirtualMachine RunsOn EsxiHost` | Dynatrace `RUNS_ON` |
| `SameAs` | simetrik | `BmcNode SameAs EsxiHost` | Dynatrace `SAME_AS` |
| `ConnectedTo` | simetrik | `HbaPort ConnectedTo SanSwitchPort`, `SanSwitchPort ConnectedTo ArrayPort` | (alana özgü — fiziksel bağlantı) |
| `BackedBy` | tüketici → sağlayıcı | `Datastore BackedBy Lun`, `Lun BackedBy StorageArray` | (alana özgü — mantıksal depolama) |
| `ManagedBy` | yönetilen → yönetici | `PhysicalServer ManagedBy OneViewAppliance`, `EsxiHost ManagedBy VCenter` | (alana özgü — yönetim düzlemi) |

**Neden `ConnectedTo` ve `BackedBy` ayrı?** Birincisi fiziksel bir kablodur ve
arızası noktasaldır (SFP, patch kablosu, port). İkincisi mantıksal bir
sağlayıcılık ilişkisidir ve arızası kapasite/erişilebilirlik sorunudur. Etki
analizi ikisini farklı yayar: kesilen bir kablo yedekli yolla telafi edilebilir,
dolan bir LUN edilemez.

**Neden `ManagedBy` ayrı bir tip?** Önceki üründeki en incelikli operasyonel
kararlardan biri, OneView/OME `Active` alarmının iLO IML kaydının önüne
geçmesiydi; gerekçesi "IML geçmiş olaydır, OneView Active şu an kilitli donanım
sorunudur". Bu öncelik kuralının çalışması için sistemin **kimin yönetim düzlemi
olduğunu** bilmesi gerekir. `ManagedBy` bunu modele taşır; öncelik sıralaması
kod içinde gömülü bir liste olmaktan çıkar.

### 2. Kenar sınıfları ve kısıtları

| Sınıf | Tipler | Döngü | Sağlık yayılımı |
|---|---|---|---|
| Containment | `PartOf` | **Yasak** (doğrulanır) | Çocuk → ebeveyn |
| Hosting | `RunsOn`, `BackedBy`, `ManagedBy` | Yasak | Sağlayıcı → tüketici |
| Kimlik | `SameAs` | N/A (denklik) | Yayılım yok — aynı şey |
| Fiziksel | `ConnectedTo` | **Serbest** | Yayılım yok — etki analizi ayrı |

`ConnectedTo` döngü içerebilir çünkü bir SAN kumaşı doğası gereği graftır
(yedekli yollar döngü oluşturur — istenen durum budur). Bu yüzden sağlık
yayılımı algoritması `ConnectedTo` kenarlarını **izlemez**; onlar etki analizi
(impact) algoritmasının alanıdır. Aria'nın "döngü analitiği bozar" uyarısına
uyum, döngüyü yasaklayarak değil, döngülü kenarları yayılımdan ayırarak
sağlanır.

### 3. Kimlik çözümleme = `SameAs` üzerinde bağlı bileşenler

`SameAs` simetrik ve geçişlidir, yani bir **denklik bağıntısıdır**. Bunun
doğrudan sonucu: kimlik çözümleme bir birleşim-bulma (union-find) problemidir.

- Collector "şu kimlik işaretleriyle bir şey gördüm" der (UUID, IP, FQDN, seri
  numarası, servis etiketi).
- `IdentityResolver` eşleşen işaretler için `SameAs` kenarı üretir.
- Aynı fiziksel kutunun tüm temsilleri tek bir bağlı bileşen oluşturur.
- Sunum katmanı bileşeni tek varlık gibi gösterir; **kayıtlar yok edilmez**.

Önceki üründe bu mantık en az dört ayrı yerde (`SharesIdentity`,
`MatchesIloInventory`, `MatchesIloNode`, `OmeAlerting`) birbirinden habersiz
uygulanmıştı. Tek bir denklik bağıntısına indirgemek, kural değişikliğini tek
noktaya taşır ve "aynı kutu iki kez sayıldı" hatasını yapısal olarak
imkânsızlaştırır.

### 4. Yol (path) bir varlık değildir

Depolama yolu (`ESXi vmhba → switch → array port → LUN`) ayrı bir varlık olarak
modellenmez. Yol, `ConnectedTo` ve `BackedBy` kenarları üzerinde **türetilmiş
bir rotadır**.

Doğrudan sonucu: SPOF tespiti (tek aktif yola düşmüş LUN) özel bir alan değil,
bir graf sorgusudur — "bu LUN ile bu host arasında kaç ayrı rota var?". Yedeklilik
kaybı grafın kendisinden okunur.

### 5. Donanım bileşenleri varlıktır

PSU, fan, DIMM, RAID denetleyicisi, fiziksel disk — hepsi `PartOf` ile fiziksel
sunucuya bağlı varlıklardır; sunucunun özelliği değildirler.

Maliyet hesabı: sunucu başına ~30 bileşen × 200 sunucu ≈ 6.000 varlık. Bellek
içi graf için önemsiz. Karşılığında "hangi DIMM arızalı, ne zamandan beri,
daha önce de arızalanmış mıydı" soruları cevaplanabilir hale gelir.

### 6. Varlık yaşam döngüsü: 30 gün mezar taşı

Bir varlık envanterde görünmez olduğunda **silinmez**. `LastSeenUtc` damgasıyla
`Vanished` durumuna geçer ve **30 gün** grafta kalır.

| Durum | Anlamı | Görünürlük |
|---|---|---|
| `Active` | Son toplamada görüldü | Normal |
| `Vanished` | Görünmüyor, 30 gün içinde | Ayrı listede, sağlığı `Unknown` |
| (silinir) | 30 gündür görünmüyor | Yok |

Gerekçe:
- Host bakıma alındığında veya ağ kesintisinde varlık geçici kaybolur. Silmek,
  geri geldiğinde tüm geçmişini ve alarm sürekliliğini kaybetmek demektir.
- `Vanished` varlığın sağlığı `Healthy` değil `Unknown`'dır — README ilke 1.
  Görünmeyen bir şey sağlıklı sayılmaz.
- Açık alarmlar varlıkla birlikte korunur; alarm yaşam döngüsü kararları
  ADR-0005'te.
- 30 gün, planlı bakım pencerelerini ve tipik donanım değişim sürelerini
  kapsayacak kadar uzun, grafı şişirmeyecek kadar kısa.

## Değerlendirilen alternatifler

### Alternatif A: Serbest, adaptör başına ilişki tipleri

Her collector kendi kenar tipini tanımlar. En esnek.

Seçilmedi çünkü grafın sorgulanabilirliğini yok eder. Dynatrace'in kapalı
sözlük tercihi tam olarak bu yüzden. Etki analizi algoritması, bilmediği bir
kenar tipiyle ne yapacağını bilemez.

### Alternatif B: Yalnızca parent-child (Aria modeli)

En basit. Tek tip, tek kural, döngüsüz.

Seçilmedi çünkü fiziksel bağlantı hiyerarşik değildir. Bir switch portu ile bir
HBA arasındaki ilişkide ebeveyn kim? İkisi de değil — bu simetrik bir bağdır.
Hiyerarşiye zorlamak, ürünün farklılaştırıcı yeteneğini ifade edilemez hale
getirir.

### Alternatif C: Kimlik denkliğini birleştirme (merge) ile çözmek

Önceki ürünün yaklaşımı: iLO kaydını ESXi kaydına "katla", tek kayıt kalsın.

Seçilmedi çünkü bilgi kaybeder ve geri alınamaz. Eşleştirme yanlışsa (iki farklı
sunucu aynı sanılırsa) orijinal kayıtlar artık yoktur. `SameAs` kenarıyla
modellemek, eşleştirmeyi **iddia** haline getirir: yanlışsa kenar kaldırılır,
kayıtlar yerinde durur. Ayrıca eşleştirmenin hangi kanıta dayandığı
(UUID mi, IP mi, FQDN mi) kenarın üzerinde taşınabilir ve denetlenebilir.

### Alternatif D: Yolu (path) varlık yapmak

SPOF analizini doğrudan yapar, sorgu gerekmez.

Seçilmedi çünkü yol türetilmiş bir kavramdır ve varlık yapmak onu grafın
gerçeğiyle senkronize tutma yükü getirir. Kenarlar değiştiğinde yol varlıkları
da güncellenmeli — iki gerçek kaynağı problemi. Türetilmiş bırakmak, grafı tek
gerçek kaynağı tutar.

## Sonuçlar

### Olumlu

- İlişkiler sorgulanabilir; etki analizi ve SPOF tespiti graf sorgusu olur.
- Kimlik çözümleme tek algoritma, tek test yüzeyi.
- Yanlış kimlik eşleştirmesi geri alınabilir (kenar kaldırılır, veri durur).
- Yönetim düzlemi önceliği modelden okunur, koda gömülü değil.
- Geçici kaybolan varlıklar geçmişini korur.

### Olumsuz / kabul ettiğimiz bedel

- **Altı tip bir kısıttır.** Modellenemeyen bir ilişkiyle karşılaşırsak ADR
  yazmak gerekecek; bu kasıtlı bir sürtünmedir ama sürtünmedir.
- `SameAs` bileşenleri her sunumda çözülmeli — doğrudan liste okumaktan pahalı.
  Önbellekleme gerekebilir.
- Mezar taşları grafta gürültü yaratır; sorguların `Active` filtresi
  uygulamayı unutmaması gerekir. Varsayılan davranış `Active` olmalı.
- Donanım bileşenlerini varlık yapmak varlık sayısını ~30× artırır. Bugünkü
  ölçekte sorun değil, ama bellek profili izlenmeli.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Altı tip yetersiz kalırsa (özellikle ağ/NSX katmanı eklendiğinde) sözlük
  genişletilmeli — ama **tip eklemek**, adaptör başına serbest tipe dönmek
  değil.
- 30 günlük mezar taşı süresi sahada yanlış çıkarsa (çok kısa: bakım
  pencereleri daha uzun / çok uzun: graf şişiyor) ayarlanabilir hale
  getirilmeli.
- Varlık sayısı bellek içi grafı zorlarsa bileşen varlıklarının materyalize
  edilme politikası (yalnızca arızalıları tut) yeniden değerlendirilmeli.
