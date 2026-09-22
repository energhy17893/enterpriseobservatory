# ADR-0025: Toplayıcı yalnızca okur; koruma, temizlik ve öz-ölçü çalıştırıcınındır

- **Durum:** Kabul edildi
- **Tarih:** 2026-09-22
- **Karar verenler:** Ertuğrul Ünal
- **İlgili:** ADR-0005 (collector sözleşmesi — bu ADR §3 ve §5'i daraltır), ADR-0020,
  `reference-approaches.md` §10.2 ve §10.3

## Bağlam

21–22 Eylül 2026 denetimi çalıştırıcı katmanını (`SourceRunner`) sağlam, onun
iki yanını açık buldu. Bulunan her açık, doğru kurulmuş bir korumanın **yan
yoldan atlanmasıydı**:

| Açık | Atlanan koruma | Nasıl atlandı |
|---|---|---|
| T0.3 | devre kesici | olay okuyucusu `SourceRunner`'dan hiç geçmiyordu |
| T0.2 | devre kesici | durumu ancak depo yazımı başarılıysa ilerliyordu |
| T0.1 | kısmi başarı modeli | ayrıştırılamayan yanıt boş bir başarı oluyordu |
| T0.5 | sunucu tarafı temizliği | iptal edilmiş token'la çalışıyordu |
| T1.2 | öğrenilen batch boyutu | her okumada toplayıcının içinde yeniden yaratılıyordu |
| #53 | tek oturum | bayrak kilidin dışında temizleniyordu |

ADR-0005 toplayıcıya `HttpClient`'ı, oturumu, temizliği ve kendi hafızasını
bıraktı, ve yasakları "mimari testlerle kısmen, kod incelemesiyle tamamen"
zorlamayı öngördü. Kod incelemesi altısını da kaçırdı.

Referans çatılar (§10.2) bunu incelemeyle değil **yapıyla** çözüyor: OTel'de
yazar yalnızca bir scrape fonksiyonu verir, denetleyici zamanlar ve sarar;
Telegraf'ta yalnızca `Gather`; Datadog'da yalnızca `check()` ve çalıştırıcının
tuttuğu istatistiklere kontrol dokunamaz. Üçünde de eklentinin elinde
atlayabileceği bir şey yoktur.

M6 (Redfish) ikinci toplayıcıyı getiriyor. Bugünkü sözleşmeyle yazarı on iki
ayrı alışkanlığı yeniden keşfedecek ya da kopyalayacak.

## Karar

**Bir toplayıcı, kendisine verilen korumalı bir oturum üzerinden okuyan ve bir
sonuç tipi dönen saf bir fonksiyona indirgenir; zamanlama, zaman aşımı, yeniden
deneme, kesici, temizlik, öğrenilen durum ve öz-ölçü çalıştırıcıya taşınır.**

1. Toplayıcı `HttpClient` almaz. Aldığı tek taşıma tutamacı zaten zaman aşımı,
   yeniden deneme ve kesiciyle sarılıdır. Envanter, gözlem ve **olay** okuması
   aynı tutamacı alır.
2. Sonuç bir toplam tiptir: `Ok(veri)`, `Partial(veri, okunamayanlar)`,
   `Failed(sebep)`. Boş bir `Ok`, toplayıcının "burada hiçbir şey yok" demesidir
   ve ayrıştırma hatasından üretilemez.
3. Sunucu tarafında yaratılan her nesne (view, collector, sayfalama token'ı)
   çalıştırıcının kapsamına **kaydedilir**; çalıştırıcı okuma nasıl biterse
   bitsin taze ve sınırlı bir token'la yok eder. Oturumun açılması, yoklanması,
   tek kilit altında yenilenmesi ve kapanışta sonlandırılması çalıştırıcınındır.
4. Kesici durumu ve kaynak başına öğrenilen değerler (batch boyutu, yüksek su
   işaretleri için depo sorgusu) çalıştırıcının belleğindedir. Depoya yazım
   eşzamansızdır; kesici sıcak yolda depoyu okumaz.
5. Çalıştırıcı kaynak ve tur başına şunları **kendisi** kaydeder: cevap verdi mi,
   süre, okunan ve okunamayan örnek, atlanan tur, son başarı, tazelik, tutulan
   oturum sayısı, sunucu–yerel saat farkı.
6. Depo portu sınırlı bir kuyruğun arkasındadır; kuyruk boyutla **ve örnek
   yaşıyla** sınırlıdır; düşen sayılır ve alarm olur.
7. Her toplayıcı ortak bir **sözleşme test takımını** geçer; geçmeyen birleşmez.
   İlk toplayıcıdan itibaren zorunludur.

Kapsam dışı: toplayıcının yetki ve yasakları (ADR-0005 §3 aynen kalır: kimlik
kararı vermez, başka katmanın varlığını yaratmaz, veri uydurmaz); envanter ile
gözlemin ayrı ritimleri (§1 kalır); `WaitForUpdatesEx` (§10.3: incelenen dört
toplayıcının hiçbiri kullanmıyor — yalnızca ölçüm gerektirirse).

## Gerekçe

- **Açıkların sınıfını kapatır, örneklerini değil.** Yukarıdaki altı satırın
  altısı da bu tasarımda yazılamaz: atlanacak yol yoktur.
- **Referans çatıların üçü de aynı yere varmış** ve plugin ekosistemlerini
  böyle dürüst tutuyor (§10.2).
- **M6'dan önce.** İkinci toplayıcı bu sözleşmeye karşı yazılırsa bir kez
  yazılır.
- **D paketi bedavaya gelir.** Öz-ölçüyü her toplayıcıya ayrı ayrı eklemek
  yerine çalıştırıcı bir kez üretir.

## Değerlendirilen alternatifler

### Alternatif A: Bugünkü sözleşme + daha çok test

Her açık için bir regresyon testi; T-P0 ve kontrol #3 böyle kapandı.

Seçilmedi çünkü test bulunmuş açığı korur, bulunmamışı değil. Altı açığın
hiçbiri yazıldığı gün test eksikliğinden değil, atlanabilir bir yol olduğundan
doğdu; yedincisi de öyle doğar.

### Alternatif B: Ortak bir taban sınıf (`CollectorBase`)

Toplayıcılar ortak davranışı kalıtımla alır.

Seçilmedi çünkü taban sınıfın metodunu çağırmak yazarın **seçimidir**; olay
okuyucusu çalıştırıcıyı nasıl atladıysa taban metodu da öyle atlar. Yetkinin
toplayıcıda hiç bulunmaması gerekiyor.

### Alternatif C: OpenTelemetry Collector'ı gömmek

Kanıtlanmış çatıyı doğrudan kullanmak.

Seçilmedi çünkü Go'da yazılmış ayrı bir süreçtir; ürün tek süreçli bir .NET
dağıtımıdır (ADR-0001), ve veri modelimiz (topoloji, kimlik işaretleri,
yapılandırma) OTel'in metrik/iz/log modeline sığmıyor. Deseni alıyoruz,
ürünü değil.

## Sonuçlar

### Olumlu

- Korumayı atlayan bir toplayıcı derlenmez.
- Öz-izleme (T1.8) tek yerde üretilir ve her toplayıcı için aynıdır.
- Redfish toplayıcısı bir okuma fonksiyonu ve bir sözleşme takımı koşusudur.
- Depo kapalıyken örnekler sessizce düşmez.

### Olumsuz / kabul ettiğimiz bedel

- **vSphere toplayıcısının yeniden şekillenmesi.** `VsphereClient` 1700+
  satırdır ve oturumu, temizliği, HTTP'yi kendi tutuyor; bunların çalıştırıcıya
  taşınması T-P2'nin F paketini bir taşıma işinden bir yeniden yazıma çevirir.
  Sözleşme takımı (E) **önce** yazılır ki taşımanın davranışı değiştirmediği
  gösterilebilsin.
- Çalıştırıcı büyür ve ürünün en kritik sınıfı olur; hatası her toplayıcıyı
  etkiler. Karşılığında tek bir yerde test edilir.
- Sınırlı kuyruk bellek tüketir ve bir boyut kararı ister; disk taşması ayrı bir
  karardır ve bu ADR onu vermiyor.
- M6 bu iş bitene kadar başlamaz.

### Bu kararı yeniden değerlendirmemiz gereken durum

- Bir satıcı API'si korumalı oturum soyutlamasına sığmıyorsa (uzun ömürlü bir
  akış, sunucudan itme) — o zaman tutamaç genişletilir, toplayıcıya ham istemci
  geri verilmez.
- Çalıştırıcı tek süreçte darboğaz olursa (ADR-0005 §6'nın collector-grup
  deseni).