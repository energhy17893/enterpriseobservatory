# Referans Ürünlerden Benimsemeler

Bu belge, piyasadaki olgun ürünlerin **zaten çözdüğü** problemleri kaydeder ve
her biri için ne benimsediğimizi, neyi reddettiğimizi ve neden'ini yazar.

## Neden var

20 Eylül 2026'da kullanıcı şunu söyledi: *"çok deneme yanılma yapıyoruz,
halihazırda piyasada bulunan referans ürünlerdeki yaklaşımlardan benimsemeler
yapabiliriz."*

Haklıydı, ve nerede haklı olduğu önemli. O güne kadarki en pahalı hata —
datastore performans sayaçlarının Datastore nesnesinde değil **host'ta**
yaşaması — dört ardışık yanlış düzeltmeye ve bir güne mal oldu. Bu, ölçülmesi
gereken bir şey değildi; bilinen bir şeydi.

Buna karşılık şunlar **hiçbir belgede yoktu** ve gerçekten ölçüm istedi:

- Bu estate'te SIOC kapalı olduğu için `datastoreVMObservedLatency` her yerde 0
- All-flash dizide gecikmenin tam milisaniyeye kesilip 0 okunması
- `storagePath`'in aynı sayaç grubunda **iki ayrı instance sözlüğü** kullanması

**Kural:** önce referans (*bilinen nedir*), sonra ölçüm (*burada ne doğru*).
İkisi çelişirse ölçüm kazanır ve çelişki
[canlı doğrulama](live-verification.md)'ya yazılır.

Bu belge körü körüne kopyalama listesi değil. **Reddedilen yaklaşım da kayda
değer** — çoğu zaman daha değerli, çünkü neden farklı olduğumuzu söyler.

---

## 1. Sağlık yayılımı — vROps yaymıyor, Dynatrace yayıyor

Bu, bizde **açık bir karardı** ve iki referans birbiriyle çelişiyor. Çelişme
biçimi cevabı veriyor.

### vROps / Aria Operations

Badge Health **yalnızca alarmlardan** hesaplanıyor: nesneye karşı açılmış, en
şiddetli *Health etkili* alarm badge'i belirler (Critical %25, Immediate %50,
Warning %75, alarm yoksa %100).

Ve kritik olan cümle:

> *"Alerts (of any Impact) affect only the object against which they are
> defined, which is to say an ESXi Host could be healthy (Badge Health 100%),
> but every VM on it unhealthy (Badge Health 25%)."*

**Otomatik yayılım yok.** İlişkiler "influence" sağlıyor — Broadcom'un kendi
ifadesiyle *"When objects are related, a problem with one object appears as an
influence on related objects"* — ama bu badge devralma değil, analitiğe girdi.

### Dynatrace

Tam tersi. Davis, dikey (vertical) ve yatay (horizontal) topolojik bağımlılığı
birlikte analiz ediyor ve **dikey etki gerçekten yayılıyor**: bir host üzerinde
sağlıksız süreçler çalışıyorsa host da sağlıksız işaretleniyor.

### Neden farklılar, ve biz hangisiyiz

Fark, **varlıkların ne olduğunda**:

| | Dynatrace | vROps | Biz |
|---|---|---|---|
| Varlıklar | Tek bir çalışan sistemin katmanları (process → host → service) | Bağımsız arıza alanları olan altyapı (VM → host → cluster) | **Altyapı** |
| Çocuk ebeveyn hakkında ne söyler | Çok şey — sağlıksız process, host'u şüpheli yapar | Az şey — hasta bir VM host hakkında bir şey söylemez | — |

**Bizim alanımız vROps'un alanı, Dynatrace'inki değil.** Ve cluster örneği bunu
kesinleştiriyor: bir cluster, host'larının toplamı değildir — **bir host'un
düşmesini soğurmak için var olan** bir HA/DRS sınırıdır. Kritik bir host'u
cluster'a yaymak, HA tam da görevini yaparken cluster'ı bozuk göstermek olur.

### Benimsenen

**Sağlık, o nesneye karşı açılmış en şiddetli alarmdan türer. Yayılım yok.**

Bu, bugün fiilen olan davranış — ama kazara: `RelationshipRules.PropagatesHealth`
tanımlı, testli ve **üretimde çağrısız**, cluster'lar ilk döngüden beri
`Unknown`. Artık kaza değil, karar.

### Reddedilen ve düzeltilen

**ADR-0004'ün Aria öncülü yanlış okunmuş.** Orada şöyle diyor:

> *"**Çocuğun sağlığı ebeveynin sağlığını etkiler** — sağlık yayılımı
> containment kenarlarını izler."*

Aria'nın yaptığı bu değil. Aria ilişkileri **influence ve analitik** için
kullanıyor, badge devralma için değil. ADR düzenlenmez; bu düzeltme
[ADR-0018](adr/0018-health-derives-from-alerts-not-propagation.md)'e yazıldı —
orada ADR-0004'ün hangi kısmının ayakta kaldığı da tek tek sayılıyor.

**Ama `ImpactFlow` kalmalı.** Bizde `RelationshipRules.ImpactFlow` zaten var ve
`EventCorrelation` tarafından **kullanılıyor** — yani Dynatrace tarzı etki
analizi korelasyon için zaten yerinde. vROps'un ayrımı tam olarak budur:
*influence/korelasyon ≠ sağlık badge'i.* İkisi ayrı kalır.

---

## 2. Saklama kademeleri — vROps'ta ham veri hiç saklanmıyor

ADR-0017'yi ölçümle yazdık. Referans, şeklin farklı olabileceğini gösteriyor.

| | vROps | Biz (ADR-0017) |
|---|---|---|
| Ham örnek | 20 sn toplanır, 5 dakikalığa katlanır katlanmaz **atılır** | 30 sn, **2 gün** saklanır |
| Orta kademe | 5 dakika, **6 ay** (varsayılan) | 5 dakika, **30 gün** |
| Kaba kademe | 1 saat, **36 ay** (varsayılan) | 1 saat, **90 gün** |

**Düzeltme — kaba kademe satırı.** Bu tabloda önceden *"1 saat, 10 yıla kadar"*
yazıyordu. Birincil belgeden doğrulandığında sayı bu değil: Aria Operations'ın
global ayarlarında *"Time Series Data Retention"* beş dakikalık veriyi
varsayılan **6 ay** tutuyor, *"Additional Time Series Retention"* ise onu saatlik
kovaya katlayıp **36 ay daha** saklıyor. Yani varsayılan toplam ufuk üç buçuk
yıl civarı, on yıl değil. "10 yıla kadar" büyük olasılıkla ayarın üst sınırından
geliyordu; varsayılanla karıştırılmıştı. Karşılaştırmayı varsayılanlar üzerinden
yapmak gerekir, çünkü bizim ADR-0017'deki sayılarımız da varsayılan.

İki şey çıkıyor:

**Ham pencere bizim farkımız.** vROps ham örneği hiç tutmuyor; ürünü 5
dakikalık ortalama. Bizim 2 günlük ham penceremiz "saat 03:14'te tam olarak ne
oldu" sorusuna cevap veriyor ve vROps bu soruya cevap veremiyor. Bu bir
eksiklik değil, **bilinçli bir ayrım** — ve satış argümanı olarak yazılmalı.

**5 dakikalık kademede fazla cimriyiz.** vROps ona 6 ay veriyor; biz 30 gün.
Ve mantıklı: o kademe "bu yamadan sonra mı başladı" sorusunun kademesi, ve yama
döngüleri aylıktır. 30 gün, bir önceki yamanın öncesine bakmaya yetmez.

### Benimsenen

**5 dakikalık kademe kısaltılmayacak.** Kullanıcı "5 dakikalık kademeye de
bakalım" dediğinde ilk içgüdü kısaltmaktı (13,6 GB'ın 8,5 GB'ı orası). Referans
tersini söylüyor: olgun ürün o kademeye **altı kat fazla** veriyor, çünkü en
çok soruyu o cevaplıyor.

**Açık kalan:** 30 günden 60 veya 90 güne *çıkarmak* gerekir mi? Maliyeti
seri başına 288 satır/gün. Bu, ölçülüp ayrı karar verilecek.

### Reddedilen

**Ham pencereyi atmak.** vROps'un yolu ucuz ama olay incelemesinde saniye
seviyesini tamamen kaybettiriyor. Bizim ürün tezimiz teşhis; ham pencere
kalıyor.

### 2.1. vCenter'ın kendi istatistikleri vROps'unkiyle aynı şey değil

Bu ikisi sürekli birbirine karıştırılıyor ve karışmanın sebebi tesadüfi bir
çakışma: her ikisinde de "5 dakika" diye bir sayı var ve **aynı şeyi
anlatmıyorlar.**

vCenter'ın kendi istatistik alt sistemi bağımsız bir mekanizma ve vROps onu
kullanmıyor — vROps 20 saniyelik örnekleri doğrudan platformdan toplayıp kendi
kademelerini kuruyor. vCenter'ın kendi kademeleri şunlar:

| Kademe | Saklama |
|---|---|
| Gerçek zamanlı, 20 sn | ~1 saat |
| 5 dakika | 1 gün |
| 30 dakika | 1 hafta |
| 2 saat | 1 ay |
| 1 gün | 1 yıl |

Üstüne bir de **istatistik seviyesi** (1–4, varsayılan **1**) var: seviye, hangi
sayaçların o kademeye hiç yazılacağını belirliyor. Yani vCenter'da bir sayacın
geçmişte olmaması iki ayrı sebepten olabilir — kademe süresi dolmuş olabilir ya
da o sayaç o seviyede **hiç toplanmamış** olabilir.

Bizi ilgilendiren pratik sonuç: vCenter'ın "5 dakika, 1 gün"ü ile vROps'un
"5 dakika, 6 ay"ı karşılaştırılabilir sayılar değil. Bizim beş dakikalık
kademamizin referansı vROps'unki; vCenter'ınki bizim *toplama* tarafımızın
kısıtı, *saklama* tarafımızın değil.

### 2.2. vROps dışındaki referanslar: yaş kademesi aslında geri çekiliyor

vROps ve bizim tasarımımız veriyi **yaşına göre** kademelere ayırıyor. Diğer
referanslara bakıldığında bunun evrensel bir şekil olmadığı, hatta modern
ürünlerde terk edilmekte olduğu görünüyor.

| Ürün | Şekil | Saklama |
|---|---|---|
| **Dynatrace (Metrics Classic)** | 4 kademe: 0–14 g 1 dk, 14–28 g 5 dk, 28–400 g 1 sa, 400 g–5 yıl 1 gün | — |
| **Dynatrace (Grail, güncel)** | **Yaş kademesi yok** — tüm geçmiş 1 dakika | 15 ay varsayılan, 10 yıla uzatılabilir |
| **Datadog** | Yaş kademesi yok, depolama çözünürlüğü 1 saniye | Düz **15 ay** |
| **Prometheus** | Aşağı örnekleme **hiç yok** | 15 gün varsayılan |
| **Thanos** | ham / 5 dk / 1 sa | Varsayılan `0d` = sonsuz |
| **Grafana Mimir** | Aşağı örnekleme yok | Varsayılan `0s` = kapalı |

Burada kaydedilmesi gereken üç ayrıntı var, çünkü üçü de kolayca yanlış
alıntılanıyor:

**Dynatrace'in dört kademeli tablosu artık eski.** O tablo Metrics Classic'e
ait. Güncel Grail'de yaş kademesi *yok*: sorgu ne kadar geriye giderse gitsin
çözünürlük bir dakika. Yani "olgun APM ürünleri de yaşa göre kademeliyor"
cümlesi bugün artık doğru değil ve bizim kademeli tasarımımızın savunması
Dynatrace'e dayanamaz — vROps'a dayanır.

**Datadog'un aşağı örneklemesi depolama zamanında değil, sorgu zamanında.**
15 ayın tamamı saniye çözünürlüğünde duruyor; kabalaştırma sorgu cevaplanırken
yapılıyor. Bunun belgelenmiş kanıtı Nested Queries özelliği: iç sorgu geçmiş bir
aralıkta daha ince çözünürlük çekebiliyor — kademeli depolamada bu mümkün
olmazdı, çünkü ince veri diskte olmazdı.

**Prometheus'un bayrağı değişti.** `--storage.tsdb.retention.time` komut satırı
bayrağı artık **kullanımdan kaldırılmış** durumda; yerini yapılandırma
dosyasındaki `storage.tsdb.retention.time` alanı aldı. Bizim belgelerimizde ya
da karşılaştırmalarımızda bayrak biçimini kullanmamak gerekiyor.

### 2.3. Thanos üzerine üç düzeltme

Thanos, aşağı örneklemeyi *bizimkine en çok benzeyen* şekilde yapan referans, ve
tam da bu yüzden hakkında dolaşan yanlışlar bizi yanlış yöne çekebilir.

**Düzeltme 1 — "önerilen piramit" diye bir şey yok.** Üçüncü taraf yazılarda
sıkça tekrarlanan *"Thanos ham 30–40 gün / 5 dk 90 gün / 1 sa 365 gün önerir"*
piramidi **Thanos belgelerinde geçmiyor.** Bu folklor. Thanos'un kendi yazdığı
tavsiye bunun tersi: *her aşağı örnekleme seviyesi için saklama süresi aynı
olmalı.* Yani Thanos kademeleri birbirinin yerine geçen bir piramit olarak değil,
aynı zaman aralığının farklı çözünürlükteki kopyaları olarak görüyor.

**Düzeltme 2 — Thanos'a göre aşağı örnekleme yer kazandırmaz.** Belgeleri bunu
açıkça söylüyor: *"downsampling doesn't save you any space"*. Tam tersine
depolamayı **~3 kat artırabiliyor**, çünkü her seviye kendi toplam (aggregate)
parçalarını ekliyor. Thanos için aşağı örneklemenin amacı yer değil, **uzun
aralıklı sorguların hızı.**

> **Bu alıntıyı bizim tasarımımıza uygulamak yanlış olur.** Fark yapısal:
> Thanos her kademeyi **sonsuza kadar yan yana tutuyor**, biz ince kademeyi
> süresi dolunca **siliyoruz.** Thanos'ta seviye eklemek toplama işlemidir,
> bizde yer değiştirme. Dolayısıyla ADR-0017'nin "her kademe kendinden
> öncekinden daha çok satır tutuyordu" aritmetiği ve 20,9 GB → 13,6 GB hesabı
> **aynen geçerli.** Bu cümle burada, alıntının yanına, yanlış uygulanmasın
> diye yazılıyor.

**Düzeltme 3 — kademeler arasında sıralama kısıtı var.** Thanos 5 dakikalık
aşağı örneklemeyi verinin **40. saatinde**, 1 saatliği **10. gününde** yapıyor.
Eğer 5 dakikalık kademenin saklama süresi 10 günden kısaysa, 5 dakikalık bloklar
1 saatlik geçiş çalışmadan önce silinir ve **1 saatlik kademe hiç oluşmaz.**
Bizde katlama yolu farklı (kovalar ham veriden ilerleyen bir su işaretiyle
üretiliyor, bloklardan değil) ama kısıtın öğrettiği şey genel: *bir kademenin
kaynağı, o kademe üretilmeden silinemez.* Saklama sürelerini ayrı ayrı
yapılandırılabilir yaptığımız için bu bizde de kurulabilir bir tuzak.

### 2.4. Ödünç alınabilir kural: çözünürlüğü yaş değil, sorgu adımı seçsin

Thanos'un sorgu tarafındaki kuralı tek satır:

```
max_source_resolution = step / 5
```

Yani 5 dakikalık veri ancak `step >= 25 dakika` olduğunda, 1 saatlik veri ancak
`step >= 5 saat` olduğunda kullanılıyor. Seçim tamamen **sorgunun adımına**
bakıyor; verinin yaşına bakmıyor.

Bizim `SeriesRetentionPolicy.RetainedResolutionFor` metodumuz ikisine birden
bakıyor: aralık/`maxPoints` çiftinden nokta bütçesine sığan en ince kademeyi
seçiyor, sonra **o kademenin saklama süresi pencerenin başına yetişiyor mu**
diye ayrıca kontrol ediyor. Oradaki yaş kontrolü bir seçim ölçütü değil, bir
erişilebilirlik kapısı — Thanos'ta olmamasının sebebi de bu: Thanos hiçbir
kademeyi silmediği için yaş hiçbir kademeyi erişilemez yapmıyor.

Yani bu bir kusur değil, farklı bir saklama modelinin gerektirdiği ek koşul.
Ödünç alınabilecek olan, `maxPoints` yerine **açık bir `step` parametresi**
düşünmek: bugün çözünürlük çağıranın nokta bütçesinden dolaylı olarak çıkıyor,
Thanos'ta ise çağıran ne kadar kabalık istediğini doğrudan söylüyor. Bu bir
karar değil, **düşünülecek bir şey** olarak kaydediliyor.

---

## 3. Uygunluk bulgusu — vROps'ta ayrı bir kavram *değil*

[Analiz katmanı önerisi](proposals/analysis-layer.md) §3'te `Finding`'i
`AlertInstance`'dan **ayrı bir kayıt** olarak önermiştim. Referans bunu
sorguluyor.

vROps'ta uygunluk (compliance) ayrı bir nesne değil: **alarm tipinin bir alt
türü.** Bir uygunluk kıstası tanımlamak için alarm tanımının `Alert Subtype`
alanı `Compliance` yapılıyor; gerisi aynı alarm makinesi.

Ve vROps'un alarm modeli bizimkinden **bir katman daha zengin**:

```
Symptom (koşul + Wait Cycle + Cancel Cycle)
   └─> Alert (bir veya daha çok symptom'un bileşimi)
         └─> Recommendation (ne yapılmalı)
```

### Benimsenen

**Ayrı `Finding` kaydı önerisi geri çekiliyor.** İki sebep:

1. vROps aynı ihtiyacı alt türle çözüyor ve ikinci bir yaşam döngüsü yazmıyor.
2. **Zaten sahip olduğumuz bir şeyi icat ediyordum.** Önerinin asıl gerekçesi
   "süreli kabul" idi — kim, neden, ne zamana kadar. Ürün bunu zaten yapıyor ve
   adı **Silence**: *"Mute until a deadline, after which it returns on its
   own."* Paralel bir kavram uydurmaya gerek yok.

Yani: uygunluk bulguları **alarm olarak** üretilecek, `Category` ile ayrılacak,
arayüzde ayrı sekmede gösterilebilecek — ama tek yaşam döngüsü, tek tablo, tek
denetim izi.

### Benimsenmesi önerilen (yeni)

**Symptom / Alert ayrımı.** Bizim `AlertDefinition` düz: koşul ve alarm tek
şey. vROps'unki iki katmanlı ve bu, çapraz metrik kurallarının doğal şekli:

> "Kuyruk derinliği 64 **ve** bekleyen IO sürekli 200" → iki symptom, bir alarm

Mimarinin §6'sı zaten bunu istiyordu ("kural motoru, eşik motorunun üst
kümesidir"). vROps'un modeli o cümlenin hazır bir uygulaması.

`HysteresisPolicy` bizde zaten var — vROps'un Wait/Cancel Cycle'ının karşılığı.
Eksik olan, bir alarmın **birden çok symptom'dan** oluşabilmesi.

---

## 4. Sayaç → nesne eşlemesi

Pahalı dersin kaynağı. vim25'te **bir sayacın nesnesi, adından çıkarılamaz**:
`datastore.*` sayaçları Datastore nesnesinde değil HostSystem'de yaşıyor,
volume instance'da adlandırılıyor.

Bu, vROps/Aria pratiğinde bilinen bir şey — çünkü vROps'un kendi collector'ı da
aynı platformdan aynı şekilde okuyor.

### Benimsenen

**Yeni bir sayaç eklenmeden önce `--map` çıktısına bakılır.** Zaten kural
([sayaç haritası](collectors/vsphere-counter-map.md) §6.4) ve zaten bir
`CounterNamesExistTests` var. Referans bunu doğruluyor: bu bir bizim
tuhaflığımız değil, platformun şekli.

---

## 5. Dinamik eşik — yöntem benimsenebilir, **depolamamız desteklemiyor**

Referans net: sabit eşik iki yerde kırılıyor — **heterojenlik** (aynı %CPU
farklı host'ta farklı şey demek) ve **döngüsellik** (haftalık/aylık desenler).
Önerilen en iyi uygulama ikisini birden kullanmak: *sabit operasyonel eşikler +
kaydedilmiş geçmiş taban çizgisi + minimum etki kapısı + soğuk başlangıç
koruması + sürdürülen süre.*

### Dynatrace'in somut yöntemi

| | |
|---|---|
| Referans penceresi | Son **7 gün**, dakikalık ölçümler |
| Taban çizgisi | Ölçümlerin **99. yüzdeliği** |
| Dalgalanma | **Çeyrekler arası açıklık** (IQR: P75 − P25) |
| Eşik | `taban + (n × dalgalanma)`, `n` hassasiyet ayarı |
| Tetikleme | Kayan pencere: **5 dakikanın 3'ü** eşiği aşmalı |

Bu **ML değil**, yüzdelik aritmetiği — yani ilke 1'i ihlal etmiyor: eşik
operatöre gösterilebilir ("P99(7g) + 2×IQR = 4,2 ms"). Mimarinin §11'i ML'i
reddediyor; bunu reddetmiyor.

### Çarpışma

**Yüzdelik hesaplayamıyoruz.** ADR-0012 bunu kabul edilmiş bedel olarak
yazmış: kova başına beş sayı (min/max/sum/count/last) saklanıyor ve *"p95
gecikme, saklanan beş sayıdan hesaplanamaz."*

Yani referansın yöntemi, ham pencerenin (2 gün) dışında **uygulanamaz** —
ve Dynatrace 7 gün kullanıyor, çünkü haftalık döngü ancak orada görünür.

ADR-0012 bu anı öngörmüş: *"Yüzdelik gerektiğinde: eskiz tabanlı bir özet yeni
bir ADR ile eklenir."* Tetik koşulundayız.

### Üç yol, hiçbiri bedava

| Yol | Ne verir | Maliyet | Sorun |
|---|---|---|---|
| **A. Sadece ham pencere** | Tam yüzdelik, şema değişmez | Sıfır | 2 gün, **haftalık döngüyü göremez** — ki sabit eşiğin başarısız olduğu yer tam orası |
| **B. Kareler toplamı** (`sum_of_squares`) | Tam **σ**, her yeniden toplamada kesin | Kova satırına +8 bayt (**%5**) | σ, sağa çarpık gecikme dağılımında zayıf eşiktir; Dynatrace bu yüzden P99+IQR kullanıyor |
| **C. Eskiz** (t-digest / DDSketch) | Sınırlı hatayla yüzdelik, birleştirilebilir | Kova başına yüzlerce bayt — kova depolamasını **2–4 kat** | Uygulama yükü; 13,6 GB → ~25-40 GB |

**B'nin çekici yanı:** varyans `(sum, sum², count)` üçlüsünden **tam** olarak
çıkar ve kovalar birleştiğinde üçü de toplanır — yani mevcut tasarımın "yeniden
toplama kesindir" doktrinine birebir uyar. Altıncı sayı, taban çizgisini açan
sayı olurdu.

**B'nin sorunu dürüstçe:** gecikme dağılımları sağa çarpıktır ve σ, aykırı
değerlerle şişer — yani tam da sivrilme olduğunda eşiği *yükseltir*.

### Karar: **şimdilik hiçbiri**

Ve sebebi önemli: **ilk kuralların hiçbiri eşik istemiyor.**

- R1 (SIOC körlüğü) — olgu, eşik değil
- R3 (yol hatası) — `busResets > 0`; SCSI, dizi meşgul diye bus reset atmaz
- R4 (HA kapalı) — yapılandırma okuması
- R2 (bir volume tek host'tan yavaş) — **eş karşılaştırması**, geçmiş değil:
  "bu host'un bu volume için gecikmesi, diğer dokuz host'un medyanının 5 katı"
  — aynı andaki akranlara bakar, taban çizgisine değil

Yani dinamik eşik, ilk kuralları **engellemiyor**. Şema değişikliğini
gerektiğinde, gerektiren kuralla birlikte yapmak; şimdi yapmak, henüz hangi
şeklin lazım olduğunu bilmeden bir kademe eklemek olur — ADR-0017'de günlük
kademe için verilen kararın aynısı.

**Açık karar, tetiği belli:** geçmişe dayalı bir taban çizgisi isteyen ilk
kural yazıldığında A/B/C arasında seçim yapılır ve yeni bir ADR'ye girer.

---

## 6. Yokluk — altı üründen altısı "kural sustu"yu "her şey yolunda"dan ayırıyor

Soru şuydu: bir kural o turda **hiç çalışmadıysa**, o kuralın açık alarmlarına ne
olmalı? Referans taraması bu konuda alışılmadık biçimde nettir — bakılan altı
ürünün **hepsi** bu ayrımı yapıyor ve **hiçbiri sessizliği sağlık saymıyor.**
Altıda altı çıkan başka bir başlık yok.

| Ürün | Mekanizma |
|---|---|
| **Grafana** | `Error` ve `No Data` birinci sınıf kural durumları. Kaybolan bir seri son durumunu **iki değerlendirme aralığı** korur, sonra `grafana_state_reason: MissingSeries` ek açıklamasıyla kapanır |
| **Prometheus** | Patlayan kural için staleness işareti **yazılmaz**: `/alerts` alarmı hâlâ firing gösterirken `/rules` aynı kuralı `health: "err"` gösterir. Ama akışın ilerisinde `ValidUntil = 4 × max(interval, resendDelay)` var — kaynak yorumu *"iki değerlendirme veya Alertmanager gönderim hatasına izin ver"* — yani sürekli patlayan bir kural, alarmlarını ~4 aralık sonra **sessizce çözer** |
| **Zabbix** | Trigger *state* (Normal/Unknown), *value*'dan (OK/PROBLEM) bağımsız bir eksen. `Unknown` ifade boyunca yayılır (`1 and Unknown → Unknown`) ve kendi dahili olaylarını üretir |
| **Netdata** | `UNDEFINED` (0), `CLEAR` (1)'den ayrı bir durum. Kuralın kaldırılması bile kendi sonlandırıcı durumunu alır |
| **Dynatrace** | Kural başına *"eksik veride alarm üret"* seçimi — ve kendi belgeleri, seyrek serilerde bunu açmanın alarm fırtınası yarattığı uyarısını yapıyor |
| **vROps** | İptal, koşulun **false olmasını** gerektirir (Cancel Cycle). Yokluk hiçbir iptal yolu değil; kendi *"veri alınmıyor"* alarmını alır |

Prometheus'un satırı en öğretici olanı, çünkü ürünün **iki ucu birbiriyle
çelişiyor**: değerlendirici tarafı dürüst (kural sağlığı ayrı yayınlanıyor,
alarm haritasına dokunulmuyor), bildirici tarafı ise zaman aşımıyla kapatıyor.
Yani tek bir üründe hem yapılacak şey hem de kaçınılacak şey var. Bu ayrım
[yokluk önerisinde](proposals/alert-absence.md) §4'ün genel bekleme süresini
reddetme gerekçesidir.

Bu bulgunun ürüne dönüşü o öneride duruyor; burada kaydedilen, **kararın
referansla değil, referansların örtüşmesiyle** verildiğidir.

## 7. Çalışma zamanında kural denetimi — susturma ile durdurma aynı şey değil

Bu satılan bir ürün, ve gürültülü bir kuralla karşılaşan operatörün elinde bir
şey olmalı. Referanslar burada da örtüşüyor, ve örtüştükleri yer bir **ayrım**:

- **Grafana** *değerlendirmeyi duraklatmak* ile *bildirimi susturmak*'ı açıkça
  ikiye ayırıyor. Duraklatılan kural hiç koşmaz; susturulan kural koşar, durumu
  görünür, yalnızca bildirim gitmez.
- **Netdata** aynı ayrımı aynı biçimde yapıyor: DISABLE değerlendirmeyi
  durdurur, SILENCE bildirimi durdurur.
- **vROps** ayrımı kapsam ekseninde zenginleştiriyor: symptom ve alarm tanımları
  bir **policy** içinde etkinleştirilip devre dışı bırakılıyor, policy de nesne
  gruplarına bağlanıyor. Sonuç hem *kural başına* hem *nesne başına* kapsam —
  "bu kural şu cluster'da çalışmasın" ifade edilebilir bir cümle oluyor.

Neden önemli: ikisi karıştırıldığında ortaya çıkan şey tam olarak §6'nın kusuru.
Bir kuralı susturmak için değerlendirmesini durdurursan, o kuralın açık
alarmları yokluk yoluna düşer ve *"arıza geçti"* damgasıyla kapanır. Yani
duraklatma ile susturmanın ayrı olması bir arayüz zarafeti değil, **yokluk
semantiğinin doğru kalmasının şartı.** Bizde henüz ikisi de yok; geldiğinde ayrı
gelmeli.

Ölçek farkı da kaydedilmeli: vROps'un policy'si bir yönetim nesnesidir ve kendi
ekranını, devralmasını ve atamasını getirir. Bizim bugünkü ihtiyacımız
muhtemelen daha küçük — kural kimliğine göre bir anahtar, artı ADR-0013'ün
istediği atıf. Ama **kapsamın bir gün nesne grubuna genişleyeceği** bilinerek
tasarlanmalı, çünkü sonradan eklemek anahtarın şeklini değiştirir.

## 8. Doğrulanamayanlar — bilerek boş bırakılan yerler

Aşağıdakiler arandı ve **bulunamadı.** Buraya yazılmalarının sebebi, birinin
altı ay sonra aynı aramayı yapıp aynı boşluğu makul görünen bir cümleyle
doldurmasını önlemek. Bunlar "henüz bakmadık" değil, **"bakıldı, yok"** kaydıdır.

**Hiçbir üretici, bir kademe sınırının neden orada olduğunu açıklamıyor.**
Dynatrace 14/28/400 sayılarını çıplak yayınlıyor; Datadog 15 ayı gerekçesiz
veriyor; Thanos'un aşağı örnekleme tasarımı için bir tasarım önerisi (design
proposal) **mevcut değil** — özellik, projenin öneri sürecinden önce geldiği için
hiç yazılmamış. Yani bizim ADR-0017'de yaptığımız şeyin — bir sayıyı ölçümle
gerekçelendirmenin — kamusal bir emsali yok. Bu, ADR-0017'yi zayıflatmıyor;
tersine, kopyalanacak bir sayı olmadığını ve ölçmekten başka yol bulunmadığını
doğruluyor.

**Hiçbir üretici, sorgu davranışını veri yaşına göre nicelemiyor.** *"N günden
eskisinde sorguların %X'i saatlik çözünürlük kullanıyor"* biçiminde kamuya açık
hiçbir veri yok. Bu, bizim kademe sürelerini kullanım verisiyle ayarlamak
istediğimizde **kendi telemetrimizi toplamak zorunda** olacağımız anlamına
geliyor.

**Broadcom 6 aylık varsayılan için gerekçe vermiyor.** Bulunabilen en yakın şey
dolaylı: kapasite motoru mevsimsellik tespiti için *"her 5 dakikada bir veri
noktası tüketiyor"* ve haftalık/aylık periyotlara bakıyor. Bundan "6 ay, aylık
periyodu birkaç kez görebilmek için seçilmiş olabilir" **çıkarımı** yapılabilir —
ama bu bizim çıkarımımız, Broadcom'un ifadesi değil. Öyle işaretlenmiştir.

**Dynatrace, Grail'in hangi kabalaştırma çözünürlüklerini materyalize ettiğini
belgelemiyor.** "Yaş kademesi yok, tüm geçmiş 1 dakika" ifadesi belgeli; bunun
arkasında sorguyu hızlandıran önceden hesaplanmış bir kabalaştırma olup olmadığı
belgesiz. Yani §2.2'deki "yaş kademesi yok" satırı **kullanıcıya görünen
sözleşme** hakkındadır, depolama iç yapısı hakkında değil.

## Sıradaki araştırma konuları

Bir sonraki adıma geçmeden önce bakılacaklar:

| Konu | Neden | Nereye bakılacak |
|---|---|---|
| Kapasite projeksiyonu yöntemi | "12 gün sonra dolar" nasıl hesaplanıyor, hangi güven aralığıyla | vROps Capacity Analytics |
| Uygunluk kıstas içeriği | vSphere Security Configuration Guide / STIG hazır kıstaslar | Aria compliance packs |
| ~~Sorgu zamanı çözünürlük seçimi~~ | **Cevaplandı — §2.4.** Thanos `max_source_resolution = step / 5` kuralını kullanıyor; bizim ek yaş kapımızın sebebi kademe silmemiz | — |
| Kural duraklatma/susturma kapsamı | §7 ayrımı benimsenecekse anahtar nesne grubuna genişleyebilmeli | vROps policy modeli, Grafana pause/silence |

---

## Kaynaklar

- [Aria Operations — Badge Metrics](https://techdocs.broadcom.com/us/en/vmware-cis/aria/aria-operations/8-16/vmware-aria-operations-user-guide-8-16/metric-property-and-alert-definitions/metrics-definitions-in-vrealize-operations-manager/calculated-metrics/badge-metrics.html)
- [Brock Peterson — VMware Aria Operations Badge Health](https://www.brockpeterson.com/post/vmware-aria-operations-badge-health)
- [Aria Operations — Configuring Object Relationships](https://techdocs.broadcom.com/us/en/vmware-cis/aria/aria-operations/8-16/vmware-aria-operations-configuration-guide-8-16/configuring-objects/object-discovery/managing-objects-in-your-environment/configuring-object-relationships.html)
- [Dynatrace — Root cause analysis](https://docs.dynatrace.com/docs/platform/davis-ai/problem-and-root-cause/root-cause-analysis)
- [Dynatrace — Smartscape topology](https://docs.dynatrace.com/docs/discover-dynatrace/platform/smartscape)
- [Broadcom KB — Aria Operations Data Collection](https://knowledge.broadcom.com/external/article/315941/vrealize-operations-data-collection.html)
- [vXpress — Demystifying vRealize Operations Data Collection](http://vxpresss.blogspot.com/2017/08/demystifying-vrealize-operations-data.html)
- [Sunny Dua — Using data roll-ups for longer retention in vROps 6.6](https://sunnydua.com/2017/07/05/part-9-using-data-roll-ups-for-longer-retention-period-in-vrops-6-6/)
- [Aria Operations — Defining Symptoms for Alerts](https://techdocs.broadcom.com/us/en/vmware-cis/aria/aria-operations/8-16/vmware-aria-operations-configuration-guide-8-16/configuring-alerts-and-actions/symptom-definitions/defining-symptoms-for-alerts.html)
- [TOMsOps — Custom Compliance Management using vRealize Operations](https://thomas-kopton.de/vblog/?p=605)
- [Dynatrace — Auto-adaptive thresholds for anomaly detection](https://docs.dynatrace.com/docs/discover-dynatrace/platform/davis-ai/anomaly-detection/concepts/auto-adaptive-threshold)
- [LogicMonitor — Static thresholds vs. dynamic thresholds](https://www.logicmonitor.com/blog/static-thresholds-vs-dynamic-thresholds)

### Saklama ve aşağı örnekleme (§2)

> **Bağlantılar hakkında dürüstlük notu.** Aşağıdaki kaynakların *belge adları ve
> içerikleri* araştırmada birincil üretici belgesinden doğrulanmıştır. Bazı
> URL'ler ise üreticinin belge sitesindeki kanonik yoldan türetilmiştir ve
> üretici belge sitelerini yeniden düzenlediğinde kırılabilir. Bir bağlantı
> ölürse **belge adıyla aramak** gerekir; iddia belge adına bağlıdır, URL'ye
> değil.

- [Aria Operations 8.18 Configuration Guide — List of Global Settings](https://techdocs.broadcom.com/us/en/vmware-cis/aria/aria-operations/8-18/vmware-aria-operations-configuration-guide/configuring-global-settings/list-of-global-settings.html) — *Time Series Data Retention* (6 ay) ve *Additional Time Series Retention* (36 ay)
- [Broadcom KB 315941 — vRealize/Aria Operations Data Collection](https://knowledge.broadcom.com/external/article/315941/vrealize-operations-data-collection.html)
- [Broadcom KB 393839 — rollup granularity](https://knowledge.broadcom.com/external/article/393839)
- [vSphere 8.0 — Data collection intervals](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere/8-0/vsphere-monitoring-and-performance-8-0/monitoring-inventory-objects-with-performance-charts/data-collection-intervals.html)
- [vSphere 8.0 — Data collection levels](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere/8-0/vsphere-monitoring-and-performance-8-0/monitoring-inventory-objects-with-performance-charts/data-collection-levels.html)
- [Dynatrace — Grail metrics retention](https://docs.dynatrace.com/docs/discover-dynatrace/references/dynatrace-concepts/retention-periods)
- [Dynatrace — Metrics Classic data retention (legacy)](https://docs.dynatrace.com/docs/analyze-explore-automate/metrics/metric-data-points)
- [Datadog — Metrics retention and resolution](https://docs.datadoghq.com/metrics/)
- [Datadog — Nested queries](https://docs.datadoghq.com/dashboards/functions/#nested-queries)
- [Prometheus — Storage: operational aspects](https://prometheus.io/docs/prometheus/latest/storage/)
- [Thanos — Compactor: downsampling, resolution and retention](https://thanos.io/tip/components/compact.md/)
- [Thanos — Querier: max_source_resolution](https://thanos.io/tip/components/query.md/)
- [Grafana Mimir — Compactor / retention configuration](https://grafana.com/docs/mimir/latest/references/architecture/components/compactor/)

### Yokluk ve kural denetimi (§6, §7)

- [Grafana — Alert rule state and health / No Data and Error handling](https://grafana.com/docs/grafana/latest/alerting/fundamentals/alert-rules/state-and-health/)
- [Grafana — Pause alert rule evaluation / Silences](https://grafana.com/docs/grafana/latest/alerting/alerting-rules/)
- [Prometheus — Alerting rules](https://prometheus.io/docs/prometheus/latest/configuration/alerting_rules/)
- [Zabbix — Trigger state and unknown value](https://www.zabbix.com/documentation/current/en/manual/config/triggers)
- [Netdata — Health alert statuses](https://learn.netdata.cloud/docs/alerts-and-notifications/alert-configuration-reference)
- [Dynatrace — Metric events: alert on missing data](https://docs.dynatrace.com/docs/analyze-explore-automate/metrics/metric-events)
- [Aria Operations — Alert policies and object groups](https://techdocs.broadcom.com/us/en/vmware-cis/aria/aria-operations/8-18/vmware-aria-operations-configuration-guide/configuring-and-using-policies.html)
