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
kullanıyor, badge devralma için değil. ADR düzenlenmez; bu düzeltme yeni bir
ADR'ye girmeli.

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
| Kaba kademe | 1 saat, 10 yıla kadar | 1 saat, **90 gün** |

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

## Sıradaki araştırma konuları

Bir sonraki adıma geçmeden önce bakılacaklar:

| Konu | Neden | Nereye bakılacak |
|---|---|---|
| Kapasite projeksiyonu yöntemi | "12 gün sonra dolar" nasıl hesaplanıyor, hangi güven aralığıyla | vROps Capacity Analytics |
| Uygunluk kıstas içeriği | vSphere Security Configuration Guide / STIG hazır kıstaslar | Aria compliance packs |
| Sorgu zamanı çözünürlük seçimi | Bizde yeni düzeltildi; Prometheus/Grafana'nın `step` modeli | Prometheus range query |

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
