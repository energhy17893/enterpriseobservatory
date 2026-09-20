# ADR-0018: Sağlık alarmlardan türer, kenarlardan yayılmaz

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-20
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0004 (§2'deki "Sağlık yayılımı" sütununun **yerini alır**;
  taksonomi ve yaşam döngüsü kısımları aynen geçerli), ADR-0003, ADR-0007,
  README ilke 1

## Bağlam

ADR-0004, ilişki taksonomisini kurarken referans platformların yaklaşımını
özetledi ve Aria Operations hakkında iki şey söyledi:

> - İlişkiler **döngü içeremez**; döngü analitik hesaplamaları bozar.
> - **Çocuğun sağlığı ebeveynin sağlığını etkiler** — sağlık yayılımı
>   containment kenarlarını izler.

Bu iki cümlenin **birincisi doğru, ikincisi değil.**

20 Eylül 2026'da yapılan referans taraması
([referans yaklaşımlar](../reference-approaches.md) §1) Aria'nın Badge Health'ini
birincil belgeden okudu. Badge Health **yalnızca o nesneye karşı açılmış
alarmlardan** hesaplanıyor: en şiddetli *Health etkili* alarm badge'i belirliyor
(Critical %25, Immediate %50, Warning %75, alarm yoksa %100). Ve Broadcom'un
kendi ifadesi ihtimale yer bırakmıyor:

> *"Alerts (of any Impact) affect only the object against which they are
> defined, which is to say an ESXi Host could be healthy (Badge Health 100%),
> but every VM on it unhealthy (Badge Health 25%)."*

İlişkilerin sağlıkla ilişkisi var, ama başka türlü: *"When objects are related,
a problem with one object appears as an influence on related objects."*
**Influence, badge devralması değildir** — analitiğe ve kök neden çalışmasına
giren bir girdidir. ADR-0004'ün öncülü büyük olasılıkla bu iki kavramın
birbirine karışmasından doğdu: Aria ilişkileri gerçekten sağlık *analizinde*
kullanıyor, ama ebeveynin badge'ini çocuğundan **türetmiyor.**

Öncülün ADR içinde taşıdığı ağırlık şu: ADR-0004 §2'nin kenar sınıfları tablosu
bir **"Sağlık yayılımı"** sütunu içeriyor ve orada Containment için *"Çocuk →
ebeveyn"*, Hosting için *"Sağlayıcı → tüketici"* yazıyor. Bu sütun, yanlış çıkan
cümlenin doğrudan ürünüdür; ADR'de o sütunu savunan başka bir gerekçe yok.

Bir ADR kabul edildikten sonra düzenlenmez (bkz. [ADR README](README.md)). Bu
yüzden düzeltme buraya yazılıyor.

### Neden bu, "kelimesi gevşek kalmış" bir durum değil

Bu ayrımı kasten yaptım, çünkü her yanlış okuma bir ADR hak etmez. İki test
uyguladım:

1. **Cümle çıkarılsaydı ADR'de bir boşluk kalır mıydı?** Kalırdı. §2 tablosunun
   bir sütunu tamamen dayanaksız kalıyor.
2. **Cümlenin doğru versiyonu aynı sonuca mı götürürdü?** Götürmezdi. Doğru
   okuma (*alarmlardan türer, yayılmaz*) tablodaki iki hücreyi de **tersine**
   çeviriyor.

İkisi de "evet" çıksaydı bu bir düzeltme notu olurdu, ADR değil. İkisi de "hayır"
çıktığı için ADR yazılıyor.

## Karar

**Bir varlığın sağlığı, yalnızca o varlığa karşı açılmış en şiddetli alarmdan
türer. Hiçbir kenar sınıfı üzerinde sağlık yayılımı yoktur.**

ADR-0004 §2'deki kenar sınıfı tablosunun sağlık sütunu şununla değiştirilir:

| Sınıf | Tipler | Döngü | Sağlık yayılımı |
|---|---|---|---|
| Containment | `PartOf` | **Yasak** (doğrulanır) | **Yayılım yok** |
| Hosting | `RunsOn`, `BackedBy`, `ManagedBy` | Yasak | **Yayılım yok** |
| Kimlik | `SameAs` | N/A (denklik) | Yayılım yok — aynı şey |
| Fiziksel | `ConnectedTo` | **Serbest** | Yayılım yok — etki analizi ayrı |

### Kapsam: neyi kapsamıyor

Bu ADR **yalnızca sağlık badge'ini** ele alır. Aşağıdakiler değişmez:

- **ADR-0004'ün geri kalanı aynen geçerli.** Altı tipli kapalı sözlük, kenar
  sınıfları, `PartOf`/`RunsOn`/`BackedBy`/`ManagedBy` üzerindeki döngü yasağı,
  `SameAs` üzerinde bağlı bileşenlerle kimlik çözümleme, yolun varlık olmaması,
  donanım bileşenlerinin varlık olması ve 30 günlük mezar taşı — hiçbirine
  dokunulmuyor. Yanlış çıkan öncül yalnızca bir sütunu besliyordu.
- **Döngü yasağı da yerinde.** Aria hakkındaki iki cümlenin birincisi
  doğrulandı; yasağın gerekçesi sağlık yayılımı değil, analitik hesaplamaların
  kendisiydi.
- **Etki analizi ve korelasyon devam ediyor.** `RelationshipRules.ImpactFlow`
  duruyor ve `EventCorrelation` tarafından kullanılmayı sürdürüyor. vROps'un
  ayrımı tam olarak budur: *influence/korelasyon ≠ sağlık badge'i.* Bir kablo
  koptuğunda hangi VM'lerin etkilendiğini söylemek ürünün asıl vaadi; o VM'lerin
  badge'ini kırmızıya çevirmek başka bir şey.

## Gerekçe

Düzeltmenin kendisi tek başına yeterli sebep değil — "referans öyle yapmıyor",
"biz de yapmayalım" demek için yeterli değil. Yayılımsızlığı **bizim alanımızda
doğru** yapan üç şey var.

**1. Bizim varlıklarımız bağımsız arıza alanları.** Dynatrace yayıyor, ve orada
haklı: process → host → service zinciri **tek bir çalışan sistemin
katmanlarıdır**, sağlıksız bir process host'u gerçekten şüpheli yapar. Bizim
grafımız altyapı: hasta bir VM, üzerinde çalıştığı host hakkında neredeyse
hiçbir şey söylemez. Aynı mekanizma iki alanda iki farklı şey yapıyor ve biz
vROps'un alanındayız.

**2. Cluster örneği meseleyi kesinleştiriyor.** Bir cluster, host'larının
toplamı değildir — **bir host'un düşmesini soğurmak için var olan** bir HA/DRS
sınırıdır. Kritik bir host alarmını cluster'a yaymak, HA tam da görevini
yaparken cluster'ı bozuk göstermek olurdu. Yayılım burada yalnızca gereksiz
değil, **yanlış bilgi**.

**3. Yayılan sağlık, alarm merkezileştirmesiyle çelişiyor.** ADR-0007 gelen
kutusunu tek doğruluk kaynağı yaptı. Sağlık alarmlardan türerse iki görünüm
aynı olguyu anlatır ve birbirini doğrular. Yayılırsa, gelen kutusunda karşılığı
olmayan kırmızı bir nesne ortaya çıkar — operatörün "neden kırmızı?" sorusuna
üründe cevap bulamadığı hâl.

## Değerlendirilen alternatifler

### Alternatif A: Yayılımı ADR-0004'ün yazdığı gibi korumak

Öncül yanlış çıksa bile karar kendi başına savunulabilir miydi? Dynatrace'in
yaptığı bu ve olgun bir üründe işe yarıyor.

Seçilmedi çünkü gerekçe bölümündeki üç sebep alana özgü ve hiçbiri Dynatrace'in
alanında geçerli değil. Ayrıca kararın **tek yazılı dayanağı** yanlış çıkan
cümleydi; dayanağı olmayan bir kararı "yine de doğru olabilir" diye korumak,
ADR tutmanın amacını ortadan kaldırır.

### Alternatif B: Yalnızca Hosting kenarlarında yaymak

`RunsOn`/`BackedBy` üzerinde sağlayıcıdan tüketiciye yaymak — dolan bir LUN'un
üstündeki datastore'u sağlıksız yapmak sezgisel geliyor.

Seçilmedi çünkü doğru mekanizma zaten var ve daha iyisini yapıyor: dolan LUN
kendi alarmını üretir, `ImpactFlow` etkilenen tüketicileri **adlandırır**. Badge
devralması bunun yanında bilgi kaybıdır — datastore'u kırmızı yapar ama *neden*
kırmızı olduğunu söylemez, ve iki nesne için tek bir olgu iki kez sayılır. Etki
analizi ile sağlık göstergesini ayrı tutmak, ikisinin de ne söylediğini net
bırakıyor.

### Alternatif C: ADR-0004'ü yerinde düzeltmek

En küçük değişiklik: cümleyi sil, sütunu düzelt, bitsin.

Seçilmedi çünkü ADR disiplini bunu açıkça yasaklıyor: *"Kabul edilmiş bir ADR
asla silinmez ve düzenlenmez."* Sessizce fikir değiştiren bir belge kimseye bir
şey öğretmez — ve burada öğretilecek olan, kararın kendisinden daha değerli:
**bir referans ürünün belgesi, ikinci elden özetiyle değil, birincil kaynağından
okunmalı.** O ders ancak yanlış okuma görünür kalırsa aktarılır.

## Sonuçlar

### Olumlu

- Sağlık göstergesinin tek bir tanımı var ve gelen kutusuyla birebir örtüşüyor.
- HA/DRS sınırları yanlış kırmızıya boyanmıyor.
- Etki analizi ile sağlık ayrı kavramlar olarak kaldığı için ikisi de
  açıklanabilir; operatör "neden kırmızı" sorusuna her zaman bir alarm bulur.
- Bugün fiilen olan davranış **artık kaza değil, karar.**
  `RelationshipRules.PropagatesHealth` tanımlı, testli ve üretimde çağrısız —
  yani kod zaten bu ADR'nin dediğini yapıyordu, gerekçesi yazılı değildi.

### Olumsuz / kabul ettiğimiz bedel

- **Üst seviye nesneler tek başlarına az şey söyler.** Bir cluster'a bakan
  operatör, altındaki host'ların durumunu cluster badge'inden okuyamaz; alt
  nesnelerin listesine bakmak zorundadır. Bu, arayüzün çözmesi gereken bir
  problemdir (ADR-0007'nin gelen kutusu bunu zaten çözüyor) ama bir bedeldir.
- **Çağrısız kalan kod bir soru işareti bırakıyor.** `PropagatesHealth`
  bayrağı artık bilinçli olarak kullanılmıyor; bayrağın kaldırılması mı yoksa
  "bu ADR gereği kullanılmıyor" diye belgelenmesi mi gerektiği ayrı bir
  değişikliğin konusudur. Ölü kod, kararı okumamış birinin bir gün "unutulmuş"
  sanıp bağlaması riskidir.
- **Cluster sağlığı bugün `Unknown` ve bu ADR onu kendiliğinden düzeltmiyor.**
  Yayılım hiç çalışmadığı için cluster'lar ilk döngüden beri `Unknown`
  durumunda. Bu ADR'nin kuralı uygulanırsa — *sağlık, nesneye karşı açılmış
  alarmlardan türer* — alarmı olmayan bir cluster `Healthy` olmalıdır, `Unknown`
  değil. `Unknown` yalnızca ADR-0004 §6'nın `Vanished` durumuna ve hiç
  toplanmamış nesnelere aittir (README ilke 1: görünmeyen sağlıklı sayılmaz —
  ama *alarmsız görünen* sağlıklıdır). Aradaki fark bir uygulama boşluğudur ve
  bu ADR'nin doğrudan gerektirdiği tek kod işidir.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Grafa **tek bir çalışan sistemin katmanları** girerse (ör. uygulama/servis
  seviyesi, konteyner iş yükleri) Dynatrace'in alanına yaklaşmış oluruz ve
  yayılım o alt graf için yeniden tartışılmalı — ama o zaman bile *o alt graf
  için*, genel bir kural olarak değil.
- Sahada "cluster'a bakıp durumu anlayamıyorum" şikâyeti gelirse, cevap yayılımı
  geri getirmek değil, **türetilmiş bir özet** olmalıdır (ör. "9 host'un 1'inde
  kritik alarm") — bu, badge'i kirletmeden aynı soruyu cevaplar. Yayılım
  yalnızca bu özet de yetmezse tekrar açılır.
